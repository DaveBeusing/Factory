namespace Factory;

public static class TestEngineering
{
    public static async Task<string> AuditAsync(StateStore store, ProjectInfo project, IAgentRuntime runtime,
        bool enqueue, CancellationToken ct, bool reviewedExistingWork = false)
    {
        WorkPackagePreflight.RequireMaster(project);
        WorkPackagePreflightReport? preflight = null;
        if (enqueue)
        {
            var state = store.Read();
            preflight = WorkPackagePreflight.InspectDirectJob(state, project,
                "Improve automated test coverage",
                "Finish existing test infrastructure and fill verified coverage gaps");
            if (preflight is null || !reviewedExistingWork)
                throw new InvalidOperationException(
                    "Inspect existing master test code and open work; explicitly acknowledge reviewed-existing-work before enqueueing audit results");
            if (GitOperations.Head(project.Path) != preflight.BaseCommit ||
                !string.IsNullOrWhiteSpace(GitOperations.Git(project.Path, "status", "--porcelain")))
                throw new InvalidOperationException(
                    "Test audit work packages require a clean master checkout");
        }
        var graph = KnowledgeGraph.Scan(project);
        var focus = graph.Take(150).Select(n => new { n.File, n.Symbols, n.Imports });
        var prompt = "Analyze likely untested behavior, risky edge cases, and integration coverage gaps. " +
            "Do not modify files or execute commands that write to the repository. " +
            "Return a concise Markdown testing audit with: coverage risks, proposed deterministic tests, " +
            "suggested mutation checks and priority. Do not invent coverage percentages. Repository index: " + Json.Stringify(focus);
        var report = await runtime.RunAsync(project.Path, prompt, "", ct, readOnly: true);
        if (!report.Success) throw new InvalidOperationException("Test audit failed: " + report.Error);
        var dir = Path.Combine(store.Home, "test-audits", project.Id.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss") + ".md");
        await File.WriteAllTextAsync(path, report.FinalText, ct);
        if (enqueue)
        {
            var job = store.AddJob(new FactoryJob
            {
                ProjectId = project.Id, Title = "Improve automated test coverage",
                Description = "Finish existing test work and implement only missing deterministic tests " +
                    "from the reviewed audit: " + report.FinalText,
                Risk = "medium", Priority = 70, ExpectedPaths = ["tests/", "test/"], Mode = "local"
            });
            store.Event(job.Id, "job_preflight", "master " + preflight!.BaseCommit +
                "; source candidates " + preflight.ExistingCode.Count +
                "; existing work " + preflight.ExistingJobs.Count +
                "; matching PRs " + preflight.OpenPullRequests.Count +
                "; PR inventory checked " + preflight.PullRequestsChecked);
        }
        return path;
    }
}
