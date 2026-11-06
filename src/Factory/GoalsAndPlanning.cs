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
    public List<PlannedPackage> Packages { get; set; } = [];
    // Source/PR evidence used when drafting the work packages. Never interpreted as completeness proof.
    public WorkPackagePreflightReport? Preflight { get; set; }
}

public static class Planning
{
    public static string PathFor(StateStore store, Guid goalId) => System.IO.Path.Combine(store.Home, "plans", $"{goalId:N}.json");
    public static async Task<FactoryPlan> ProposeAsync(StateStore store, ProjectInfo p, FactoryGoal goal, IAgentRuntime agent, CancellationToken ct)
    {
        var current = store.Read();
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
            preflight.MasterFiles
        });
        var prompt = "READ ONLY: Do not modify source files. Plan exclusively against the verified master commit. " +
            "Inspect existing source, tests, active work, and open pull requests BEFORE proposing packages. " +
            "When functionality already exists or is partially implemented, FIRST propose to finish, " +
            "repair, validate, or integrate that implementation, rather than recreate it. " +
            "Do not make a second package for work already implemented and verified. " +
            "Treat keyword matches as investigation leads, not proof that the feature exists. " +
            "If the PR inventory is unavailable, clearly record the uncertainty; never claim no conflicting PR exists. " +
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
        var path = PathFor(store, goal.Id);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Json.Stringify(plan));
        return plan;
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
        bool reviewedExistingWork = false)
    {
        if (mode is not ("local" or "remote")) throw new ArgumentException("mode must be local or remote");
        var plan = JsonSerializer.Deserialize<FactoryPlan>(File.ReadAllText(PathFor(store, goalId)), Json.Options)
            ?? throw new InvalidOperationException("Unreadable plan");
        Validate(plan);
        var snapshot = store.Read();
        var requestedGoal = snapshot.Goals.Single(g => g.Id == goalId);
        var requestedProject = snapshot.Projects.Single(p => p.Id == requestedGoal.ProjectId);
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
        if ((fresh.ExistingCode.Count > 0 || fresh.ExistingJobs.Count > 0 ||
                fresh.OpenPullRequests.Count > 0) && !reviewedExistingWork)
            throw new InvalidOperationException(
                "Existing implementation or work candidates require explicit review before adopting new work packages");
        return store.Change(state =>
        {
            var goal = state.Goals.Single(g => g.Id == goalId);
            if (goal.State != "draft") throw new InvalidOperationException("Goal already adopted");
            var project = state.Projects.Single(p => p.Id == goal.ProjectId);
            WorkPackagePreflight.RequireMaster(project);
            if (WorkPackagePreflight.MasterCommit(project) != plan.BaseCommit)
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
