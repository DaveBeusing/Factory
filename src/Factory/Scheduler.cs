namespace Factory;

public static class DependencyGraph
{
    public static bool WouldCycle(IReadOnlyList<FactoryJob> jobs)
    {
        var byId = jobs.ToDictionary(j => j.Id);
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        bool Visit(Guid id)
        {
            if (visited.Contains(id)) return false;
           if (!visiting.Add(id)) return true;
           foreach (var dep in byId[id].DependsOn)
            {
                if (!byId.ContainsKey(dep) || Visit(dep)) return true;
           }
            visiting.Remove(id); visited.Add(id);
           return false;
        }
        return byId.Keys.Any(Visit);
    }
    public static bool Conflict(FactoryJob a, FactoryJob b)
    {
        if (a.ProjectId != b.ProjectId) return false;
        // Unknown scope is conservatively treated as potentially overlapping.
        if (a.ExpectedPaths.Count == 0 || b.ExpectedPaths.Count == 0) return true;
        foreach (var x in a.ExpectedPaths)
        foreach (var y in b.ExpectedPaths)
        {
            var left = x.Replace('\\', '/').TrimEnd('/');
           var right = y.Replace('\\', '/').TrimEnd('/');
           if (left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
                left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase) ||
                right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    public static bool DependenciesSatisfied(FactoryJob job, IReadOnlyList<FactoryJob> all)
        => job.DependsOn.All(id => all.Any(other => other.Id == id && other.Integrated));
}

public sealed class JobScheduler
{
    public static IReadOnlyList<FactoryJob> Select(FactoryState state, int limit)
    {
        if (state.Paused || limit <= 0) return [];
        var active = state.Jobs.Where(j => JobStates.Active.Contains(j.State) &&
            j.LeaseUntil > DateTimeOffset.UtcNow).ToList();
        var chosen = new List<FactoryJob>();
        var capacity = Math.Max(0, limit - active.Count);
        var counts = active.GroupBy(j => j.ProjectId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var job in state.Jobs.Where(j => j.State == JobStates.Queued)
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt))
        {
            if (chosen.Count >= capacity) break;
           if (!DependencyGraph.DependenciesSatisfied(job, state.Jobs)) continue;
           if (job.GoalId.HasValue)
            {
                var goal = state.Goals.FirstOrDefault(g => g.Id == job.GoalId);
               if (goal == null || goal.State != "active" ||
                    state.Jobs.Where(j => j.GoalId == job.GoalId)
                        .Aggregate(0L, (sum, j) => checked(sum + j.TokensIn + j.TokensOut)) >
                        goal.TokenBudget - BudgetedAgentRuntime.MinimumTurnAllowance) continue;
               // One active job per goal, including remote claims. Avoid concurrent budget overspend.
                if (active.Concat(chosen).Any(j => j.GoalId == job.GoalId)) continue;
           }
            var project = state.Projects.FirstOrDefault(p => p.Id == job.ProjectId);
           if (project == null || project.Status != "active" || counts.GetValueOrDefault(job.ProjectId) >= project.MaxParallel) continue;
           if (active.Concat(chosen).Any(j => DependencyGraph.Conflict(job, j))) continue;
           chosen.Add(job);
           counts[job.ProjectId] = counts.GetValueOrDefault(job.ProjectId) + 1;
        }
        return chosen;
    }

    public static IReadOnlyList<IReadOnlyList<Guid>> Group(IReadOnlyList<FactoryJob> jobs)
    {
        if (DependencyGraph.WouldCycle(jobs)) throw new ArgumentException("Dependency cycle");
        var integrated = jobs.Where(j => j.Integrated).Select(j => j.Id).ToHashSet();
        var remaining = jobs.Where(j => !j.Integrated).ToList();
        var groups = new List<IReadOnlyList<Guid>>();
        while (remaining.Count > 0)
        {
            var group = new List<FactoryJob>();
           foreach (var job in remaining.OrderByDescending(j => j.Priority))
            {
                if (!job.DependsOn.All(integrated.Contains)) continue;
               if (group.Any(other => DependencyGraph.Conflict(job, other))) continue;
               group.Add(job);
           }
            if (group.Count == 0) throw new InvalidOperationException("Unresolved dependency or conflict graph");
           groups.Add(group.Select(j => j.Id).ToArray());
           foreach (var j in group) { integrated.Add(j.Id); remaining.Remove(j); }
        }
        return groups;
    }

    public static TimeSpan PredictDuration(FactoryJob job, IReadOnlyList<FactoryJob> history)
    {
        var durations = history.Where(h => h.ProjectId == job.ProjectId && h.StartedAt.HasValue && h.FinishedAt.HasValue &&
            h.State != JobStates.Failed && h.Risk == job.Risk)
            .Select(h => (h.FinishedAt!.Value - h.StartedAt!.Value).TotalSeconds)
            .Where(x => x > 0).OrderBy(x => x).ToArray();
        if (durations.Length == 0) return TimeSpan.FromMinutes(job.Risk == "high" ? 40 : 15);
        return TimeSpan.FromSeconds(durations[durations.Length / 2]);
    }
}
