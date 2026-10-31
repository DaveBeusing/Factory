namespace Factory;

public static class TestEngineering
{
    public static async Task<string> AuditAsync(StateStore store, ProjectInfo project, IAgentRuntime runtime,
        bool enqueue, CancellationToken ct)
    {
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
            store.AddJob(new FactoryJob
            {
                ProjectId = project.Id, Title = "Improve automated test coverage",
                Description = "Implement suitable deterministic tests from the reviewed test-audit document: " + report.FinalText,
                Risk = "medium", Priority = 70, ExpectedPaths = ["tests/", "test/"], Mode = "local"
            });
        return path;
    }
}
