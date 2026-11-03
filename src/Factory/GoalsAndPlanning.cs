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
}

public static class Planning
{
    public static string PathFor(StateStore store, Guid goalId) => System.IO.Path.Combine(store.Home, "plans", $"{goalId:N}.json");
    public static async Task<FactoryPlan> ProposeAsync(StateStore store, ProjectInfo p, FactoryGoal goal, IAgentRuntime agent, CancellationToken ct)
    {
        var graph = KnowledgeGraph.Scan(p);
        var summary = graph.Take(120).Select(n => new { n.File, n.Symbols, n.Imports });
        var prompt = "READ ONLY: Do not modify source files. Propose small independent software work packages for this objective. " +
            "Return ONLY valid JSON with a packages array, each item with title, description, risk (low|medium|high), priority (0..100), files string array and dependsOn integer array (zero-based indices). " +
            "Any assumption must be stated in description. Goal: " + goal.Objective +
            "\nAcceptance criteria:\n" + string.Join("\n", goal.AcceptanceCriteria) +
            "\nRepository index (bounded): " + Json.Stringify(summary);
        var result = await agent.RunAsync(p.Path, prompt, "", ct, readOnly: true);
        if (!result.Success) throw new InvalidOperationException("Planner failed: " + result.Error);
        var content = ExtractJson(result.FinalText);
        var plan = JsonSerializer.Deserialize<FactoryPlan>(content, Json.Options) ?? throw new InvalidOperationException("Empty plan");
        plan.ProjectId = p.Id; plan.GoalId = goal.Id; plan.BaseCommit = GitOperations.Head(p.Path);
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
    public static IReadOnlyList<FactoryJob> Adopt(StateStore store, Guid goalId, string mode)
    {
        if (mode is not ("local" or "remote")) throw new ArgumentException("mode must be local or remote");
        var plan = JsonSerializer.Deserialize<FactoryPlan>(File.ReadAllText(PathFor(store, goalId)), Json.Options)
            ?? throw new InvalidOperationException("Unreadable plan");
        Validate(plan);
        return store.Change(state =>
        {
            var goal = state.Goals.Single(g => g.Id == goalId);
            if (goal.State != "draft") throw new InvalidOperationException("Goal already adopted");
            var project = state.Projects.Single(p => p.Id == goal.ProjectId);
            if (GitOperations.Head(project.Path) != plan.BaseCommit)
                throw new InvalidOperationException("Base branch advanced; regenerate plan");
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
