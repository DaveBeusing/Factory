using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Factory;

internal sealed class ProjectViewerEndpoint
{
    internal static void Map(WebApplication app, StateStore store, FactorySettings settings)
    {
        var viewer = new ProjectViewerEndpoint();
        const string providerHolds = "/api/viewer/projects/{projectId:guid}/provider-reservations";
        app.MapGet(providerHolds, (HttpContext context, Guid projectId) =>
            PlanResponse(() => store.ReadProjectProviderHolds(context.Request.Headers.Authorization.ToString(), projectId)))
            .WithMetadata(viewer);
        app.MapPost(providerHolds + "/{reservationId:guid}/reconcile",
            (HttpContext context, Guid projectId, Guid reservationId, ProviderBudgetReconciliationInput input) =>
                PlanResponse(() => store.ReconcileProjectProviderHold(context.Request.Headers.Authorization.ToString(),
                    projectId, reservationId, input))).WithMetadata(viewer);
        const string projectRoute = "/api/viewer/projects/{projectId:guid}";
        app.MapGet("/api/viewer/projects", (HttpContext context) =>
            PlanResponse(() => store.ReadViewerProjects(context.Request.Headers.Authorization.ToString())))
            .WithMetadata(viewer);
        app.MapGet(projectRoute + "/goal-reservations", (HttpContext context, Guid projectId) =>
            PlanResponse(() => store.ReadProjectGoalHolds(context.Request.Headers.Authorization.ToString(), projectId)))
            .WithMetadata(viewer);
        app.MapPost(projectRoute + "/goals/{goalId:guid}/budget/reconcile",
            (HttpContext context, Guid projectId, Guid goalId, BudgetReconciliationInput input) =>
                PlanResponse(() => store.ReconcileProjectGoalHold(context.Request.Headers.Authorization.ToString(),
                    projectId, goalId, input))).WithMetadata(viewer);
        app.MapGet(projectRoute + "/executor-receipts", (HttpContext context, Guid projectId) =>
            PlanResponse(() => store.ReadProjectExecutorHolds(context.Request.Headers.Authorization.ToString(), projectId)))
            .WithMetadata(viewer);
        app.MapPost(projectRoute + "/jobs/{jobId:guid}/provider-turns/{turnId:guid}/recover",
            async (HttpContext context, Guid projectId, Guid jobId, Guid turnId, RemoteReceiptRecoveryInput input,
                CancellationToken cancellation) =>
            {
                try
                {
                    return Results.Ok(await store.RecoverProjectExecutorReceiptAsync(context.Request.Headers.Authorization.ToString(),
                        settings, projectId, jobId, turnId, input, cancellation));
                }
                catch (Exception ex) when (IsPlanFailure(ex)) { return PlanFailure(ex); }
            }).WithMetadata(viewer);
        app.MapGet(projectRoute + "/jobs/operations", (HttpContext context, Guid projectId) =>
            PlanResponse(() => store.ReadProjectJobOperations(context.Request.Headers.Authorization.ToString(), projectId)))
            .WithMetadata(viewer);
        app.MapPost("/api/viewer/projects/{projectId:guid}/jobs/{jobId:guid}/action",
            (HttpContext context, Guid projectId, Guid jobId, ProjectJobControlInput input) =>
            {
                try
                {
                    var result = store.ControlProjectJob(context.Request.Headers.Authorization.ToString(), projectId, jobId, input);
                    return result.RequestedState is null ? Results.Ok(result) : Results.Accepted(value: result);
                }
                catch (ScopedProjectRejected ex) { return PlanFailure(ex); }
            }).WithMetadata(viewer);
        app.MapPost("/api/viewer/projects/{projectId:guid}/jobs/{jobId:guid}/reconcile",
            (HttpContext context, Guid projectId, Guid jobId, ProjectJobControlInput input) =>
            {
                try { return Results.Ok(store.ReconcileProjectJob(context.Request.Headers.Authorization.ToString(), projectId, jobId, input)); }
                catch (ScopedProjectRejected ex) { return PlanFailure(ex); }
            }).WithMetadata(viewer);
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
        app.MapPost(planRoute + "/generate", async (HttpContext context, Guid projectId, Guid goalId,
            ScopedPlanGenerationInput input, CancellationToken cancellation) =>
        {
            try
            {
                return Results.Ok(await store.GenerateScopedPlanAsync(context.Request.Headers.Authorization.ToString(),
                    projectId, goalId, input, settings, cancellation));
            }
            catch (Exception ex) when (IsPlanFailure(ex)) { return PlanFailure(ex); }
        }).WithMetadata(viewer);
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
        catch (Exception ex) when (IsPlanFailure(ex)) { return PlanFailure(ex); }
    }

    private static bool IsPlanFailure(Exception ex) => ex is ScopedProjectRejected or ArgumentException or InvalidOperationException;

    private static IResult PlanFailure(Exception failure)
    {
        if (failure is ScopedProjectRejected ex)
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
        if (failure is ArgumentException)
            return Results.BadRequest(new { error = "Invalid plan or adoption input." });
        if (failure is TokenBudgetExceededException)
            return Results.Conflict(new { error = "The planning provider budget could not cover or settle the invocation." });
        return Results.Conflict(new { error = "Plan verification failed. Refresh the plan and review master before retrying." });
    }
}
