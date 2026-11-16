using Factory;

internal static class ProjectAdvisorySnapshotTests
{
    internal static async Task RunAsync()
    {
        var home = Directory.CreateTempSubdirectory("factory-advisory-test-");
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(home.FullName, "repository")).FullName;
            GitOperations.Git(repository, "init", "--initial-branch=master", "--template=");
            var originalFile = Path.Combine(repository, "sample.txt");
            await File.WriteAllTextAsync(originalFile, "committed master");
            GitOperations.Git(repository, "add", "sample.txt");
            GitOperations.Git(repository, "-c", "user.name=Factory Fixture", "-c",
                "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", "Fixture");
            var commit = GitOperations.Head(repository);
            GitOperations.Git(repository, "checkout", "-b", "feature/local");

            repository = GitOperations.Git(repository, "rev-parse", "--show-toplevel");
            var store = new StateStore(Path.Combine(home.FullName, "state"));
            var project = MissionControlManager.CreateProject(store, new ProjectInput { Name = "Advisory fixture", Path = repository });
            var settings = new FactorySettings
            {
                Providers = [new(), new() { Id = "claude", Kind = "claude", Model = "sonnet" }]
            };
            var assignment = new ProviderAssignment { ProjectId = project.Id, Role = "analysis" };
            var baselineWorktrees = GitOperations.Git(repository, "worktree", "list", "--porcelain");
            ProjectReviewSnapshot? latest = null;
            foreach (var providerId in new[] { "codex", "claude" })
            {
                assignment.ProviderId = providerId;
                store.Change(state =>
                {
                    state.ProviderAssignments.Clear();
                    state.ProviderAssignments.Add(assignment);
                });
                var fixture = new AdvisoryFixture(async (source, _) =>
                {
                    Check(!GitOperations.SamePath(source, repository), "Provider ran in the registered checkout");
                    Check(GitOperations.Head(source) == commit, "Provider did not open the reviewed commit");
                    await File.WriteAllTextAsync(originalFile, "workspace edit during turn");
                    return await File.ReadAllTextAsync(Path.Combine(source, "sample.txt"));
                });
                latest = await AnalyzeAsync(fixture);
                Check(latest.Commit == commit && latest.Advisory == "committed master" &&
                    latest.AdvisoryProvider == providerId, "Advisory does not reflect the committed source");
                Check(await File.ReadAllTextAsync(originalFile) == "workspace edit during turn",
                    "The registered checkout was changed by snapshot maintenance");
                CheckCleanup(fixture);
            }

            var altered = new AdvisoryFixture(async (source, _) =>
            {
                await File.WriteAllTextAsync(Path.Combine(source, "sample.txt"), "altered snapshot");
                return "should not be published";
            });
            try
            {
                await AnalyzeAsync(altered);
                throw new Exception("Changed snapshot produced an advisory");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("review source changed", StringComparison.Ordinal)) { }
            Check(ProjectReviewer.Read(store, project.Id)?.Id == latest?.Id, "The previous advisory was replaced");
            CheckCleanup(altered);

            using var cancellation = new CancellationTokenSource();
            var cancelled = new AdvisoryFixture((_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<string>(token);
            });
            try
            {
                await AnalyzeAsync(cancelled, cancellation.Token);
                throw new Exception("Cancelled review returned an advisory");
            }
            catch (OperationCanceledException) { }
            Check(ProjectReviewer.Read(store, project.Id)?.Id == latest?.Id, "Cancellation replaced the previous advisory");
            CheckCleanup(cancelled);

            Task<ProjectReviewSnapshot> AnalyzeAsync(AdvisoryFixture fixture, CancellationToken ct = default) =>
                ProjectReviewer.AnalyzeAsync(store, project.Id, ct, settings, (_, _, _) => fixture);

            void CheckCleanup(AdvisoryFixture fixture)
            {
                Check(fixture.Checkout is not null && !Directory.Exists(Path.GetDirectoryName(fixture.Checkout)),
                    "The temporary snapshot directory still exists");
                Check(GitOperations.Git(repository, "worktree", "list", "--porcelain") == baselineWorktrees,
                    "An extra worktree remains registered");
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows())
                foreach (var file in home.EnumerateFiles("*", SearchOption.AllDirectories))
                    if (file.IsReadOnly) file.IsReadOnly = false;
            home.Delete(true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class AdvisoryFixture(Func<string, CancellationToken, Task<string>> observe) : IAgentRuntime
    {
        public string? Checkout { get; private set; }

        public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
            CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
            Action<AgentTokenUsage>? onUsage = null)
        {
            Check(readOnly && previousSession.Length == 0, "Advisory turn must be fresh and read-only");
            Checkout = directory;
            return new AgentRunResult
            {
                Success = true, FinalText = await observe(directory, cancellation),
                UsageReported = true, InputTokens = 3, OutputTokens = 2
            };
        }
    }
}
