using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Factory;

public sealed class PlannedPackage
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Risk { get; set; } = "medium";
    public int Priority { get; set; } = 50;
    public List<string> Files { get; set; } = [];
    public List<int> DependsOn { get; set; } = [];
}

public sealed class FactoryPlan
{
    public Guid GoalId { get; set; }
    public Guid ProjectId { get; set; }
    public string BaseCommit { get; set; } = "";
    public string GoalRevision { get; set; } = "";
    public Guid? Revision { get; set; }
    public List<PlannedPackage> Packages { get; set; } = [];
    // Source/PR evidence used when drafting the work packages. Never interpreted as completeness proof.
    public WorkPackagePreflightReport? Preflight { get; set; }
}

public static class Planning
{
    public static string PathFor(StateStore store, Guid goalId) => System.IO.Path.Combine(store.Home, "plans", $"{goalId:N}.json");
    public static string GoalRevision(FactoryGoal goal) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Json.Stringify(new { goal.Objective, goal.AcceptanceCriteria }))));

    public static Task<FactoryPlan> ProposeAsync(StateStore store, ProjectInfo project, FactoryGoal goal,
        FactorySettings settings, CancellationToken ct, Guid? expectedPlanRevision = null)
    {
        var state = store.Read();
        var route = ProviderRouter.Select(settings, state, project.Id, "planner", "medium");
        var profile = ProviderRouter.Resolve(settings, state, project.Id, "planner");
        var runtime = new ProjectScopedAgentRuntime(route.Runtime, store, project.Id, goal.Id,
            profile, "planner", route.Model, settings.GoalReservationTokens);
        return ProposeAsync(store, project, goal, runtime, ct, expectedPlanRevision);
    }

    public static async Task<FactoryPlan> ProposeAsync(StateStore store, ProjectInfo p, FactoryGoal goal, IAgentRuntime agent, CancellationToken ct, Guid? expectedPlanRevision = null)
    {
        var current = store.Read();
        var existingRevision = MissionControlManager.ReadPlan(store, goal.Id)?.Revision;
        if (expectedPlanRevision is { } expected && expected != (existingRevision ?? Guid.Empty))
            throw new InvalidOperationException("Plan changed; reopen it before regenerating");
        var goalRevision = GoalRevision(goal);
        var preflight = WorkPackagePreflight.Inspect(current, p, goal);
        if (GitOperations.Head(p.Path) != preflight.BaseCommit ||
            !string.IsNullOrWhiteSpace(GitOperations.Git(p.Path, "status", "--porcelain")))
            throw new InvalidOperationException(
                "Planning must inspect a clean checkout at master; do not plan from a feature branch or uncommitted source.");
        var context = Json.Stringify(new
        {
            preflight.BaseBranch, preflight.BaseCommit, preflight.SearchTerms,
            preflight.ExistingCode, preflight.ExistingJobs, preflight.OpenPullRequests,
            preflight.PullRequestsChecked, preflight.PullRequestCheckNote,
            preflight.OpenIssues, preflight.IssuesChecked, preflight.IssueCheckNote,
            preflight.MasterFiles
        });
        var prompt = "READ ONLY: Do not modify source files. Plan exclusively against the verified master commit. " +
            "Inspect existing source, tests, active work, open issues and open pull requests BEFORE proposing packages. " +
            "When functionality already exists or is partially implemented, FIRST propose to finish, " +
            "repair, validate, or integrate that implementation, rather than recreate it. " +
            "Do not make a second package for work already implemented and verified. " +
            "Treat keyword matches as investigation leads, not proof that the feature exists. " +
            "If the PR or issue inventory is unavailable, clearly record the uncertainty; never claim no conflicting PR exists. " +
            "Work packages must be independently reviewable and based on master, never stacked on other PR branches. " +
            "Return ONLY valid JSON with a packages array, each item with title, description, risk (low|medium|high), " +
            "priority (0..100), files string array and dependsOn integer array (zero-based indices). " +
            "Describe existing code evidence, completion gaps and relevant PRs in each description. " +
            "Any assumption must be stated in description. Goal: " + goal.Objective +
            "\nAcceptance criteria:\n" + string.Join("\n", goal.AcceptanceCriteria) +
            "\nVerified master preflight context (candidate evidence; inspect before changing): " + context;
        var result = await agent.RunAsync(p.Path, prompt, "", ct, readOnly: true);
        if (!result.Success) throw new InvalidOperationException("Planner failed: " + result.Error);
        var content = ExtractJson(result.FinalText);
        var plan = JsonSerializer.Deserialize<FactoryPlan>(content, Json.Options) ?? throw new InvalidOperationException("Empty plan");
        plan.ProjectId = p.Id; plan.GoalId = goal.Id;
        plan.BaseCommit = preflight.BaseCommit;
        plan.Preflight = preflight;
        Validate(plan);
        plan.GoalRevision = goalRevision;
        plan.Revision = existingRevision;
        return SaveRevision(store, plan, p);
    }

    internal static FactoryPlan SaveRevision(StateStore store, FactoryPlan plan, ProjectInfo source)
    {
        // All plan writes and adoptions take the same SQLite writer lock, including
        // across CLI and dashboard processes. The file itself is replaced atomically.
        return store.Change(state =>
        {
            var goal = state.Goals.Single(g => g.Id == plan.GoalId);
            if (goal.State != "draft" || goal.ProjectId != plan.ProjectId || GoalRevision(goal) != plan.GoalRevision)
                throw new InvalidOperationException("Goal changed; reopen the current goal before saving its plan");
            var project = state.Projects.Single(p => p.Id == goal.ProjectId);
            if (project.Path != source.Path || WorkPackagePreflight.MasterCommit(project) != plan.BaseCommit)
                throw new InvalidOperationException("Project checkout or master changed; refresh the plan");
            if (MissionControlManager.ReadPlan(store, goal.Id)?.Revision != plan.Revision)
                throw new InvalidOperationException("Plan changed; reopen the latest revision before saving");
            plan.Revision = Guid.NewGuid();
            Write(store, plan);
            return plan;
        });
    }

    private static void Write(StateStore store, FactoryPlan plan)
    {
        var path = PathFor(store, plan.GoalId);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, Json.Stringify(plan));
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
    private static string ExtractJson(string raw)
    {
        var start = raw.IndexOf('{'); var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("Planner did not return JSON");
        return raw[start..(end + 1)];
    }
    public static void Validate(FactoryPlan plan)
    {
        if (plan.Packages is not { Count: > 0 and <= 50 }) throw new ArgumentException("Plan requires 1-50 packages");
        for (var index = 0; index < plan.Packages.Count; index++)
        {
            var item = plan.Packages[index];
            if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Description))
                throw new ArgumentException($"Invalid package {index}");
            if (item.Priority is < 0 or > 100) throw new ArgumentException("Priority out of range");
            if (!new[] { "low", "medium", "high" }.Contains(item.Risk)) throw new ArgumentException("Invalid risk");
            if (item.DependsOn.Any(i => i < 0 || i >= plan.Packages.Count || i == index))
                throw new ArgumentException("Invalid dependency index");
            try { WorkPackageScope.ValidateDeclaration(item.Files); }
            catch (ScopeViolationException ex)
            {
                throw new ArgumentException("Invalid work-package path contract: " + ex.Message, ex);
            }
        }
        var visits = new int[plan.Packages.Count];
        bool Walk(int i)
        {
            if (visits[i] == 1) return true;
            if (visits[i] == 2) return false;
            visits[i] = 1;
            foreach (var child in plan.Packages[i].DependsOn) if (Walk(child)) return true;
            visits[i] = 2; return false;
        }
        if (Enumerable.Range(0, visits.Length).Any(Walk)) throw new ArgumentException("Dependency cycle");
    }
    public static IReadOnlyList<FactoryJob> Adopt(StateStore store, Guid goalId, string mode,
        bool reviewedExistingWork = false, Guid planRevision = default)
    {
        if (mode is not ("local" or "remote")) throw new ArgumentException("mode must be local or remote");
        var plan = MissionControlManager.ReadPlan(store, goalId)
            ?? throw new InvalidOperationException("No saved plan");
        if (plan.Revision is null || string.IsNullOrEmpty(plan.GoalRevision))
            throw new InvalidOperationException("Plan predates revision tracking; review and save it again before adoption");
        if (planRevision == Guid.Empty || plan.Revision != planRevision)
            throw new InvalidOperationException("Review and confirm the current plan revision before adoption");
        Validate(plan);
        var snapshot = store.Read();
        var requestedGoal = snapshot.Goals.Single(g => g.Id == goalId);
        var requestedProject = snapshot.Projects.Single(p => p.Id == requestedGoal.ProjectId);
        if (plan.GoalId != goalId || plan.ProjectId != requestedProject.Id ||
            plan.GoalRevision != GoalRevision(requestedGoal))
            throw new InvalidOperationException("Goal changed; review and save or regenerate its plan before adoption");
        if (plan.Preflight is null || plan.Preflight.BaseCommit != plan.BaseCommit)
            throw new InvalidOperationException("Plan has no current master preflight; save or regenerate before adoption");
        var fresh = WorkPackagePreflight.Inspect(snapshot, requestedProject, requestedGoal);
        if (fresh.BaseCommit != plan.BaseCommit)
            throw new InvalidOperationException("master advanced since preflight; reassess and regenerate the plan");
        if (plan.Preflight.PullRequestsChecked && !fresh.PullRequestsChecked)
            throw new InvalidOperationException("Open pull requests could not be rechecked; review before adoption");
        if (fresh.PullRequestsChecked &&
            !fresh.OpenPullRequests.Select(p => p.Number).OrderBy(x => x)
                .SequenceEqual(plan.Preflight.OpenPullRequests.Select(p => p.Number).OrderBy(x => x)))
            throw new InvalidOperationException("Matching open pull requests changed; refresh preflight and plan");
        if (plan.Preflight.IssuesChecked && !fresh.IssuesChecked)
            throw new InvalidOperationException("Open issues could not be rechecked; review before adoption");
        if (fresh.IssuesChecked &&
            !fresh.OpenIssues.Select(i => i.Number).OrderBy(n => n)
                .SequenceEqual(plan.Preflight.OpenIssues.Select(i => i.Number).OrderBy(n => n)))
            throw new InvalidOperationException("Matching open issues changed; refresh preflight and plan");
        // A keyword scan cannot prove the absence of other implementations or PRs.
        // Require the operator to acknowledge the review even when no candidates matched.
        if (!reviewedExistingWork)
            throw new InvalidOperationException(
                "All new work packages require explicit review of existing master code and outstanding PRs and issues before adoption");
        return store.Change(state =>
        {
            var goal = state.Goals.Single(g => g.Id == goalId);
            if (goal.State != "draft") throw new InvalidOperationException("Goal already adopted");
            if (goal.ProjectId != plan.ProjectId || GoalRevision(goal) != plan.GoalRevision)
                throw new InvalidOperationException("Goal changed; review and save or regenerate its plan before adoption");
            if (MissionControlManager.ReadPlan(store, goalId)?.Revision != planRevision)
                throw new InvalidOperationException("Plan changed; review and confirm the current plan revision before adoption");
            var project = state.Projects.Single(p => p.Id == goal.ProjectId);
            WorkPackagePreflight.RequireMaster(project);
            if (project.Path != requestedProject.Path || WorkPackagePreflight.MasterCommit(project) != plan.BaseCommit)
                throw new InvalidOperationException("master advanced; regenerate the plan");
            var jobs = plan.Packages.Select(pkg => new FactoryJob
            {
                ProjectId = goal.ProjectId, GoalId = goalId, Title = pkg.Title, Description = pkg.Description,
                Risk = pkg.Risk, Priority = pkg.Priority, ExpectedPaths = pkg.Files, Mode = mode
            }).ToList();
            for (var i = 0; i < jobs.Count; i++)
                jobs[i].DependsOn = plan.Packages[i].DependsOn.Select(index => jobs[index].Id).ToList();
            if (DependencyGraph.WouldCycle(jobs)) throw new InvalidOperationException("Cyclic plan");
            state.Jobs.AddRange(jobs);
            goal.State = "active";
            state.Events.Add(new FactoryEvent { Type = "plan_adopted", Message = $"Goal {goalId}: {jobs.Count} work packages" });
            return (IReadOnlyList<FactoryJob>)jobs;
        });
    }
}
