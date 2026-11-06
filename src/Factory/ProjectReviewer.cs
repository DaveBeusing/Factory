using System.Text.Json;

namespace Factory;

public sealed class ProjectReviewFinding
{
    public string Id { get; set; } = "";
    public string Severity { get; set; } = "medium";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string ProposedGoal { get; set; } = "";
    public List<string> AcceptanceCriteria { get; set; } = [];
}

public sealed class ProjectReviewSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Commit { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public int IndexedFiles { get; set; }
    public List<ProjectReviewFinding> Findings { get; set; } = [];
    public string Method { get; set; } = "deterministic_repository_inspection";
    // Optional read-only provider commentary, never treated as verified evidence or an automatic goal.
    public string Advisory { get; set; } = "";
    public string AdvisoryProvider { get; set; } = "";
    public string AdvisoryModel { get; set; } = "";
}

public sealed class ReviewGoalInput
{
    public string FindingId { get; set; } = "";
    public string Objective { get; set; } = "";
    public List<string> AcceptanceCriteria { get; set; } = [];
    public long TokenBudget { get; set; } = 100000;
}

public static class ProjectReviewer
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private static string FilePath(StateStore store, Guid projectId) =>
        Path.Combine(store.Home, "project-reviews", projectId.ToString("N") + ".json");

    public static ProjectReviewSnapshot? Read(StateStore store, Guid projectId)
    {
        if (!store.Read().Projects.Any(p => p.Id == projectId))
            throw new InvalidOperationException("Project does not exist");
        var file = FilePath(store, projectId);
        return File.Exists(file)
            ? JsonSerializer.Deserialize<ProjectReviewSnapshot>(File.ReadAllText(file), Json.Options)
            : null;
    }

    public static async Task<ProjectReviewSnapshot> AnalyzeAsync(StateStore store, Guid projectId,
        CancellationToken ct, FactorySettings? settings = null)
    {
        var gate = Locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var state = store.Read();
            var project = state.Projects.Single(p => p.Id == projectId);
            var commit = GitOperations.Head(project.Path);
            var review = new ProjectReviewSnapshot { ProjectId = projectId, Commit = commit };
            void Add(string id, string severity, string category, string title, string evidence,
                string goal, params string[] criteria)
            {
                review.Findings.Add(new ProjectReviewFinding
                {
                    Id = id, Severity = severity, Category = category, Title = title,
                    Evidence = evidence, ProposedGoal = goal, AcceptanceCriteria = criteria.ToList()
                });
            }
            if (project.Checks.Count == 0)
                Add("missing-quality-gates", "high", "quality", "No project checks configured",
                    "Factory project configuration contains zero executable quality checks.",
                    "Establish deterministic build and test gates for this project",
                    "Configure at least one executable quality check",
                    "Demonstrate a passing and a failing check run",
                    "Document how the checks run in a clean checkout");
            ct.ThrowIfCancellationRequested();
            var root = project.Path;
            var hasTestDirectory = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Any(dir => Path.GetFileName(dir).Contains("test", StringComparison.OrdinalIgnoreCase));
            if (!hasTestDirectory)
                Add("test-discovery", "medium", "testing", "No top-level test directory detected",
                    "A top-level directory with 'test' in its name was not found. This heuristic does not establish the absence of tests.",
                    "Audit automated test discovery and coverage",
                    "Identify existing test projects and their locations",
                    "Add or document a repeatable test command",
                    "Record demonstrable test results");
            if (!File.Exists(Path.Combine(root, "README.md")))
                Add("missing-readme", "low", "documentation", "No root README.md detected",
                    "The repository root does not contain README.md.",
                    "Create an operational README for this repository",
                    "Document prerequisites and chronological setup",
                    "Provide verified build and test commands");
            var failures = state.Jobs.Where(j => j.ProjectId == projectId &&
                j.State is JobStates.Blocked or JobStates.Failed).ToArray();
            if (failures.Length > 0)
                Add("blocked-jobs", "high", "reliability", "Blocked or failed jobs require review",
                    failures.Length + " recorded jobs are failed or blocked. Review individual checkpoints and evidence before retry.",
                    "Resolve outstanding blocked and failed work safely",
                    "Inspect each unresolved job and classify its failure",
                    "Reconcile uncertain Git side effects and budget liabilities",
                    "Rerun required checks against the correct commit");
            ct.ThrowIfCancellationRequested();
            // Bounded native index: no untrusted repository scripts or model-generated actions.
            var index = KnowledgeGraph.Scan(project).Take(500).ToArray();
            review.IndexedFiles = index.Length;
            if (settings != null && state.ProviderAssignments.Any(x =>
                x.ProjectId == projectId && x.Role == "analysis"))
            {
                var choice = ProviderRouter.Select(settings, state, projectId, "analysis", "medium");
                var profile = ProviderRouter.Resolve(settings, state, projectId, "analysis");
                var runtime = new ProjectScopedAgentRuntime(choice.Runtime, store, projectId, null,
                    profile, "analysis", choice.Model, settings.GoalReservationTokens);
                var boundedIndex = Json.Stringify(index.Take(80).Select(x => new { x.File, x.Symbols, x.Imports }));
                if (boundedIndex.Length > 24000) boundedIndex = boundedIndex[..24000];
                var prompt = "READ ONLY: Provide advisory repository review observations based ONLY on the bounded " +
                    "index and verified deterministic findings provided below. Do not claim direct validation, " +
                    "execution, security certification or exhaustive coverage. Cite paths where supplied. " +
                    "State uncertainty plainly. No code changes or external side effects. " +
                    "Repository index: " + boundedIndex + "\nDeterministic findings: " +
                    Json.Stringify(review.Findings);
                var response = await runtime.RunAsync(project.Path, prompt, "", ct, readOnly: true);
                if (!response.Success) throw new InvalidOperationException("Project analysis provider failed: " + response.Error);
                review.Advisory = response.FinalText[..Math.Min(12000, response.FinalText.Length)];
                review.AdvisoryProvider = choice.Id;
                review.AdvisoryModel = choice.Model;
            }
            if (GitOperations.Head(root) != commit)
                throw new InvalidOperationException("Repository advanced during review; rerun against a stable commit");
            var file = FilePath(store, projectId);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, Json.Stringify(review), ct);
                File.Move(temp, file, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            store.Event(null, "project_review_created",
                "Project " + projectId + " at " + commit + ": " + review.Findings.Count + " findings");
            return review;
        }
        finally { gate.Release(); }
    }

    public static FactoryGoal CreateDraftGoal(StateStore store, Guid projectId, ReviewGoalInput input)
    {
        var review = Read(store, projectId)
            ?? throw new InvalidOperationException("Analyze the project before creating review-derived goals");
        var project = store.Read().Projects.Single(p => p.Id == projectId);
        if (GitOperations.Head(project.Path) != review.Commit)
            throw new InvalidOperationException("Review is stale: repository HEAD advanced; run analysis again");
        var finding = review.Findings.SingleOrDefault(f => f.Id == input.FindingId)
            ?? throw new InvalidOperationException("Unknown review finding");
        var objective = string.IsNullOrWhiteSpace(input.Objective) ? finding.ProposedGoal : input.Objective;
        var criteria = input.AcceptanceCriteria is { Count: > 0 }
            ? input.AcceptanceCriteria : finding.AcceptanceCriteria;
        var goal = MissionControlManager.CreateGoal(store, new GoalInput
        {
            ProjectId = projectId, Objective = objective,
            AcceptanceCriteria = criteria, TokenBudget = input.TokenBudget
        });
        store.Event(null, "review_goal_created",
            "Review " + review.Id + " finding " + finding.Id + " -> goal " + goal.Id);
        return goal;
    }
}
