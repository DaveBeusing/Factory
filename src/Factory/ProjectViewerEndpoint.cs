using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Factory;

internal sealed class ProjectViewerEndpoint
{
    internal static void Map(WebApplication app, StateStore store)
    {
        var viewer = new ProjectViewerEndpoint();
        const string planRoute = "/api/viewer/projects/{projectId:guid}/plans/{goalId:guid}";
        app.MapGet(planRoute, (HttpContext context, Guid projectId, Guid goalId) =>
            PlanResponse(() => store.ReadScopedPlan(context.Request.Headers.Authorization.ToString(), projectId, goalId)))
            .WithMetadata(viewer);
        app.MapPut(planRoute, (HttpContext context, Guid projectId, Guid goalId, FactoryPlan input) =>
            PlanResponse(() => store.SaveScopedPlan(context.Request.Headers.Authorization.ToString(), projectId, goalId, input)))
            .WithMetadata(viewer);
        app.MapPost(planRoute + "/adopt", (HttpContext context, Guid projectId, Guid goalId, PlanAdoptInput input) =>
            PlanResponse(() => store.AdoptScopedPlan(context.Request.Headers.Authorization.ToString(), projectId, goalId, input)))
            .WithMetadata(viewer);
        app.MapPost("/api/viewer/projects/{projectId:guid}/goals",
            (HttpContext context, Guid projectId, ProjectDraftGoalInput input) =>
                WriteDraft(store, context, projectId, null, input)).WithMetadata(viewer);
        app.MapPut("/api/viewer/projects/{projectId:guid}/goals/{goalId:guid}",
            (HttpContext context, Guid projectId, Guid goalId, ProjectDraftGoalInput input) =>
                WriteDraft(store, context, projectId, goalId, input)).WithMetadata(viewer);
        app.MapGet("/api/viewer/projects/{projectId:guid}", (HttpContext context, Guid projectId) =>
        {
            var (authenticated, summary) = store.ReadViewerSummary(context.Request.Headers.Authorization.ToString(), projectId);
            if (!authenticated) return Results.Unauthorized();
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        }).WithMetadata(viewer);

        app.MapGet("/api/viewer/projects/{projectId:guid}/jobs/{jobId:guid}/quality",
            (HttpContext context, Guid projectId, Guid jobId) =>
            {
                var (authenticated, job) = store.ReadViewerJobEvidence(
                    context.Request.Headers.Authorization.ToString(), projectId, jobId);
                if (!authenticated) return Results.Unauthorized();
                if (job is null) return Results.NotFound();
                try
                {
                    var evidence = JobEvidenceReader.ReadViewer(store, job);
                    return evidence is null ? Results.NotFound() : Results.Ok(evidence);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    ArgumentException or InvalidOperationException or JsonException)
                {
                    return Results.NotFound();
                }
            }).WithMetadata(viewer);
    }

    private static IResult WriteDraft(StateStore store, HttpContext context, Guid projectId, Guid? goalId,
        ProjectDraftGoalInput input)
    {
        var result = store.WriteProjectDraftGoal(context.Request.Headers.Authorization.ToString(), projectId, goalId, input);
        return result.Status switch
        {
            DraftGoalStatus.Unauthorized => Results.Unauthorized(),
            DraftGoalStatus.NotFound => Results.NotFound(),
            DraftGoalStatus.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
            DraftGoalStatus.Invalid => Results.BadRequest(new { error = result.Error }),
            DraftGoalStatus.Conflict => Results.Conflict(new { error = result.Error }),
            DraftGoalStatus.Saved when goalId.HasValue => Results.Ok(result.Goal),
            DraftGoalStatus.Saved => Results.Created($"/api/viewer/projects/{projectId}", result.Goal),
            _ => throw new InvalidOperationException("Unexpected draft goal result")
        };
    }

    private static IResult PlanResponse(Func<object> operation)
    {
        try { return Results.Ok(operation()); }
        catch (ScopedPlanRejected ex)
        {
            return ex.Status switch
            {
                DraftGoalStatus.Unauthorized => Results.Unauthorized(),
                DraftGoalStatus.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                DraftGoalStatus.NotFound => Results.NotFound(),
                DraftGoalStatus.Invalid => Results.BadRequest(new { error = ex.Message }),
                _ => Results.Conflict(new { error = ex.Message })
            };
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "Invalid plan or adoption input." });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { error = "Plan verification failed. Refresh the plan and review master before retrying." });
        }
    }
}
