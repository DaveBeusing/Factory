namespace Factory;

public sealed record AcceptanceEvidence(string Criterion, string Check);
public sealed record IntegratedPrerequisite(Guid JobId, string HeadCommit, string IntegratedCommit);
public sealed record SpecificationChange(string Kind, string Package,
    PlannedPackage? Before, PlannedPackage? After);

internal static class SpecificationContracts
{
    internal static bool CanRevise(FactoryGoal goal, IReadOnlyList<FactoryJob> jobs)
    {
        if (goal.State == "draft") return true;
        if (goal.State != "active") return false;
        var current = jobs.Where(job => job.GoalId == goal.Id).ToArray();
        return current.Length > 0 && current.All(job => job.State == JobStates.Queued &&
            job.Attempt == 0 && job.LeaseOwner.Length == 0) &&
            !jobs.Any(job => job.GoalId != goal.Id && current.Any(old => job.DependsOn.Contains(old.Id)));
    }

    internal static void ValidateNew(FactoryPlan plan)
    {
        if (plan.ContractVersion != 1)
            throw new ArgumentException("Save new plan revisions as specification contract version 1.");
        Planning.Validate(plan);
    }

    internal static void Validate(FactoryPlan plan)
    {
        if (plan.ContractVersion == 0) return;
        if (plan.ContractVersion != 1)
            throw new ArgumentException("Unsupported specification contract version.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in plan.Packages)
        {
            package.Title = package.Title.Trim();
            if (!names.Add(package.Title) || package.DependsOn.Distinct().Count() != package.DependsOn.Count)
                throw new ArgumentException("Package names and dependency entries must be unique.");

            package.Files = package.Files.Select(WorkPackageScope.Normalize).ToList();
            for (var i = 0; i < package.Files.Count; i++)
            for (var j = 0; j < i; j++)
                if (WorkPackageScope.Includes(package.Files[i], package.Files[j]) ||
                    WorkPackageScope.Includes(package.Files[j], package.Files[i]))
                    throw new ArgumentException("A package cannot declare overlapping owned paths.");

            if (package.AcceptanceEvidence is not { Count: > 0 and <= 100 } ||
                package.AcceptanceEvidence.Any(e => e is null || string.IsNullOrWhiteSpace(e.Criterion) ||
                    string.IsNullOrWhiteSpace(e.Check)))
                throw new ArgumentException("Each package needs acceptance criteria mapped to named checks.");
            package.AcceptanceEvidence = package.AcceptanceEvidence
                .Select(e => new AcceptanceEvidence(e.Criterion.Trim(), e.Check.Trim())).Distinct().ToList();
            if (package.IntegratedPrerequisites is null || package.IntegratedPrerequisites.Any(p =>
                p is null || p.JobId == Guid.Empty || !IntegrationVerifier.IsCommitSha(p.HeadCommit) ||
                !IntegrationVerifier.IsCommitSha(p.IntegratedCommit)) ||
                package.IntegratedPrerequisites.Select(p => p.JobId).Distinct().Count() != package.IntegratedPrerequisites.Count)
                throw new ArgumentException("External prerequisites need a unique job and exact commit.");
        }

        var dependencies = new HashSet<int>?[plan.Packages.Count];
        HashSet<int> Dependencies(int from)
        {
            if (dependencies[from] is { } known) return known;
            var result = new HashSet<int>();
            foreach (var direct in plan.Packages[from].DependsOn)
            {
                result.Add(direct);
                result.UnionWith(Dependencies(direct));
            }
            return dependencies[from] = result;
        }
        for (var i = 0; i < plan.Packages.Count; i++)
        for (var j = 0; j < i; j++)
            if (!Dependencies(i).Contains(j) && !Dependencies(j).Contains(i) &&
                plan.Packages[i].Files.Any(a => plan.Packages[j].Files.Any(b =>
                    WorkPackageScope.Includes(a, b) || WorkPackageScope.Includes(b, a))))
                throw new ArgumentException("Packages with overlapping ownership require an explicit dependency.");
    }

    internal static void Verify(FactoryPlan plan, ProjectInfo project, IReadOnlyList<FactoryJob> jobs)
    {
        if (plan.ContractVersion == 0) return;
        foreach (var package in plan.Packages)
        {
            foreach (var path in package.Files)
            foreach (var restricted in project.ProtectedPaths)
                if (WorkPackageScope.Includes(path, restricted) || WorkPackageScope.Includes(restricted, path))
                    throw new InvalidOperationException("A specification cannot own protected paths.");
            foreach (var evidence in package.AcceptanceEvidence)
                if (!project.Checks.Any(c => c.Name == evidence.Check))
                    throw new InvalidOperationException("The specification references an unavailable project check: " + evidence.Check);
            foreach (var prerequisite in package.IntegratedPrerequisites)
                if (!jobs.Any(j => j.Id == prerequisite.JobId && j.ProjectId == project.Id &&
                    j.Integrated && j.State == JobStates.Integrated &&
                    string.Equals(j.HeadSha, prerequisite.HeadCommit, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("A specification prerequisite is not verified integrated at the declared commit.");
        }
    }

    internal static List<SpecificationChange> Diff(FactoryPlan? previous, FactoryPlan current)
    {
        var result = new List<SpecificationChange>();
        var older = previous?.Packages ?? [];
        for (var i = 0; i < Math.Max(older.Count, current.Packages.Count); i++)
        {
            var before = i < older.Count ? older[i] : null;
            var after = i < current.Packages.Count ? current.Packages[i] : null;
            if (Json.Stringify(before) != Json.Stringify(after))
                result.Add(new(before is null ? "added" : after is null ? "removed" : "changed",
                    (after ?? before)!.Title, before, after));
        }
        return result;
    }
}
