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
        if (SchedulingResources.Conflict(a, b)) return true;
        // Unknown scope is conservatively treated as potentially overlapping.
        if (a.ExpectedPaths.Count == 0 || b.ExpectedPaths.Count == 0) return true;
        try
        {
            foreach (var x in a.ExpectedPaths)
            foreach (var y in b.ExpectedPaths)
                if (WorkPackageScope.Includes(x, y) || WorkPackageScope.Includes(y, x))
                    return true;
        }
        catch (ScopeViolationException)
        {
            // Invalid contracts cannot safely be scheduled in parallel.
            return true;
        }
        return false;
    }
    public static bool DependenciesSatisfied(FactoryJob job, IReadOnlyList<FactoryJob> all)
        => job.DependsOn.All(id => all.Any(other => other.Id == id && other.ProjectId == job.ProjectId &&
            other.Integrated && other.State == JobStates.Integrated));
}

public static class JobExecutionModes
{
    public const string Local = "local";
    public const string Remote = "remote";
    public static bool IsSupported(string mode) => mode is Local or Remote;
    public static bool Matches(FactoryJob job, string mode)
        => IsSupported(mode) && string.Equals(job.Mode, mode, StringComparison.Ordinal);
}

public sealed class JobScheduler
{
    // Execution mode is part of the eligibility contract; the safe default is local.
    public static IReadOnlyList<FactoryJob> Select(FactoryState state, int limit,
        string executionMode = JobExecutionModes.Local, Func<FactoryJob, bool>? eligibility = null,
        int reservationTokens = GoalBudgetLedger.DefaultReservationTokens) =>
        SelectWithReasons(state, limit, executionMode, eligibility, reservationTokens, null);

    internal static IReadOnlyList<FactoryJob> SelectWithReasons(FactoryState state, int limit,
        string executionMode, Func<FactoryJob, bool>? eligibility, int reservationTokens,
        ICollection<SchedulingDecision>? decisions)
    {
        if (!JobExecutionModes.IsSupported(executionMode))
            throw new ArgumentException("Unsupported execution mode", nameof(executionMode));
        if (reservationTokens < BudgetedAgentRuntime.MinimumTurnAllowance)
            throw new ArgumentOutOfRangeException(nameof(reservationTokens));
        var now = DateTimeOffset.UtcNow;
        var active = state.Jobs.Where(j => (JobStates.Active.Contains(j.State) || j.LeaseOwner.Length != 0) &&
            j.LeaseUntil > now).OrderBy(j => j.Id).ToList();
        var chosen = new List<FactoryJob>();
        var capacity = Math.Max(0, limit - active.Count);
        var counts = active.GroupBy(j => j.ProjectId).ToDictionary(g => g.Key, g => g.Count());
        // Plan hypothetical reservations so selection cannot oversubscribe a goal
        // even before each selected job is atomically claimed in SQLite.
        var planned = new Dictionary<Guid, long>();
        foreach (var job in state.Jobs.Where(j => j.State == JobStates.Queued &&
            JobExecutionModes.Matches(j, executionMode))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt).ThenBy(j => j.Id))
        {
            if (state.Paused) { decisions?.Add(new(job.Id, "queue_paused")); continue; }
            if (chosen.Count >= capacity) { decisions?.Add(new(job.Id, "global_capacity")); if (decisions is null) break; continue; }
            if (job.LeaseOwner.Length != 0) { decisions?.Add(new(job.Id, "lease_owned")); continue; }
            if (eligibility is not null && !eligibility(job)) { decisions?.Add(new(job.Id, "worker_ineligible")); continue; }
            if (!DependencyGraph.DependenciesSatisfied(job, state.Jobs) ||
                !job.DependsOn.All(state.VerifiedIntegrations.Contains)) { decisions?.Add(new(job.Id, "dependency_not_integrated")); continue; }
           long allocation = 0;
           if (job.GoalId is { } goalId)
           {
               allocation = GoalBudgetLedger.Allocation(state, job, reservationTokens);
               var available = allocation == 0 ? 0 :
                   GoalBudgetLedger.Available(state, goalId) - planned.GetValueOrDefault(goalId);
               allocation = Math.Min(allocation, Math.Max(0, available));
               if (allocation < BudgetedAgentRuntime.MinimumTurnAllowance) { decisions?.Add(new(job.Id, "goal_budget")); continue; }
           }
            var project = state.Projects.FirstOrDefault(p => p.Id == job.ProjectId);
           if (project == null || project.Status != "active") { decisions?.Add(new(job.Id, "project_inactive")); continue; }
           if (counts.GetValueOrDefault(job.ProjectId) >= project.MaxParallel) { decisions?.Add(new(job.Id, "project_capacity")); continue; }
           var blocker = state.Jobs.Where(j => j.Id != job.Id &&
                (j.LeaseOwner.Length != 0 || j.State == JobStates.Blocked && j.Stage is "reconcile" or "interrupted_git"))
                .OrderBy(j => j.Id).Concat(active).Concat(chosen)
                .FirstOrDefault(j => DependencyGraph.Conflict(job, j));
           if (blocker is not null) { decisions?.Add(new(job.Id, "ownership_conflict", blocker.Id)); continue; }
           chosen.Add(job);
           decisions?.Add(new(job.Id, "eligible"));
           if (job.GoalId is { } selectedGoal)
               planned[selectedGoal] = checked(planned.GetValueOrDefault(selectedGoal) + allocation);
           counts[job.ProjectId] = counts.GetValueOrDefault(job.ProjectId) + 1;
        }
        return chosen;
    }

    public static IReadOnlyList<IReadOnlyList<Guid>> Group(IReadOnlyList<FactoryJob> jobs)
    {
        if (DependencyGraph.WouldCycle(jobs)) throw new ArgumentException("Dependency cycle");
        var integrated = jobs.Where(j => j.Integrated && j.State == JobStates.Integrated).Select(j => j.Id).ToHashSet();
        var remaining = jobs.Where(j => !integrated.Contains(j.Id)).ToList();
        var groups = new List<IReadOnlyList<Guid>>();
        while (remaining.Count > 0)
        {
            var group = new List<FactoryJob>();
           foreach (var job in remaining.OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedAt).ThenBy(j => j.Id))
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
