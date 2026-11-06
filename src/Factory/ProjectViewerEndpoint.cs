using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Factory;

internal sealed class ProjectViewerEndpoint
{
    internal static void Map(WebApplication app, StateStore store)
    {
        var viewer = new ProjectViewerEndpoint();
        app.MapGet("/api/viewer/projects/{projectId:guid}", (HttpContext context, Guid projectId) =>
            WithProject(store, context, projectId, (snapshot, project) =>
            {
                var goals = snapshot.Goals.Where(goal => goal.ProjectId == project.Id).ToArray();
                var goalIds = goals.Select(goal => goal.Id).ToHashSet();
                var jobs = snapshot.Jobs.Where(job => job.ProjectId == project.Id);
                var usage = snapshot.ProviderUsages.Where(entry => entry.ProjectId == project.Id);
                return Results.Ok(new
                {
                    project = new { project.Id, project.Name, project.Status },
                    goals = goals.Select(goal => new { goal.Id, goal.Objective, goal.State, goal.TokenBudget }),
                    jobs = jobs.Select(job => new
                    {
                        job.Id, job.Title, job.State,
                        goalId = job.GoalId is Guid id && goalIds.Contains(id) ? job.GoalId : null
                    }),
                    usage = usage.GroupBy(entry => entry.ProviderId).Select(group => new
                    {
                        providerId = group.Key,
                        inputTokens = group.Sum(entry => entry.InputTokens),
                        outputTokens = group.Sum(entry => entry.OutputTokens),
                        reportedCostMicros = group.Any(entry => entry.ReportedCostMicros.HasValue)
                            ? group.Sum(entry => entry.ReportedCostMicros) : null
                    })
                });
            })).WithMetadata(viewer);

        app.MapGet("/api/viewer/projects/{projectId:guid}/jobs/{jobId:guid}/quality",
            (HttpContext context, Guid projectId, Guid jobId) =>
                WithProject(store, context, projectId, (snapshot, project) =>
                {
                    var job = snapshot.Jobs.FirstOrDefault(job => job.ProjectId == project.Id && job.Id == jobId);
                    if (job is null) return Results.NotFound();
                    try
                    {
                        var evidence = JobEvidenceReader.Read(store, job, includeDetails: false);
                        return evidence is null ? Results.NotFound() : Results.Ok(evidence);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        ArgumentException or InvalidOperationException or JsonException)
                    {
                        return Results.NotFound();
                    }
                })).WithMetadata(viewer);
    }

    private static IResult WithProject(StateStore store, HttpContext context, Guid projectId,
        Func<FactoryState, ProjectInfo, IResult> read)
    {
        var (authenticated, snapshot) = store.ReadViewerProject(context.Request.Headers.Authorization.ToString(), projectId);
        if (!authenticated) return Results.Unauthorized();
        var project = snapshot?.Projects.FirstOrDefault(project => project.Id == projectId);
        return project is null ? Results.NotFound() : read(snapshot!, project);
    }
}
