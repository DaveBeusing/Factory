namespace Factory;

/// <summary>
/// Controller-side eligibility for untrusted remote workers. Registration and a valid
/// bearer token establish identity only; neither creates project grants nor attestations.
/// </summary>
public static class WorkerAuthorization
{
    private static readonly HashSet<string> KnownCapabilities = new(StringComparer.Ordinal)
    {
        "native-agent", "git-cli", "runtime:exec", "runtime:app-server",
        "native-checks", "container-checks"
    };

    public static bool IsKnownCapability(string? capability) =>
        capability is not null && KnownCapabilities.Contains(capability);

    public static HashSet<string>? RequiredCapabilities(
        ProjectInfo project, FactoryJob job, FactorySettings settings)
    {
        if (settings.Runtime is not ("exec" or "app-server"))
            return null;

        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "native-agent", "git-cli", "runtime:" + settings.Runtime
        };
        if (project.Checks.Any(check => string.IsNullOrWhiteSpace(check.ContainerImage)))
            required.Add("native-checks");
        if (project.Checks.Any(check => !string.IsNullOrWhiteSpace(check.ContainerImage)))
            required.Add("container-checks");

        foreach (var capability in project.RequiredExecutionCapabilities
                     .Concat(job.RequiredExecutionCapabilities))
        {
            if (!IsKnownCapability(capability))
                return null; // Unknown requested protections must never be downgraded.
            required.Add(capability);
        }
        return required;
    }

    public static bool CanAccessProject(FactoryState state, string workerId, Guid projectId) =>
        state.Workers.Any(w => w.Id == workerId && w.Enabled) &&
        state.WorkerProjectGrants.Any(g => g.WorkerId == workerId && g.ProjectId == projectId);

    public static bool CanExecute(FactoryState state, WorkerInfo worker,
        ProjectInfo project, FactoryJob job, FactorySettings settings)
    {
        if (!worker.Enabled || worker.Draining || job.ProjectId != project.Id ||
            !CanAccessProject(state, worker.Id, project.Id))
            return false;

        var required = RequiredCapabilities(project, job, settings);
        if (required is null)
            return false;

        var now = DateTimeOffset.UtcNow;
        var verified = state.WorkerCapabilityProofs
            .Where(p => p.WorkerId == worker.Id && IsKnownCapability(p.Capability) &&
                !string.IsNullOrWhiteSpace(p.Evidence) &&
                p.VerifiedAt <= now && p.VerifiedAt > now.AddDays(-30))
            .Select(p => p.Capability)
            .ToHashSet(StringComparer.Ordinal);

        return required.IsSubsetOf(verified);
    }
}
