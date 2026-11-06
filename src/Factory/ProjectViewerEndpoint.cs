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
}
