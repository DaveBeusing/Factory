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
        "native-checks", "container-checks", RemoteProviderRouting.Capability
    };

    public static bool IsKnownCapability(string? capability) =>
        capability is not null && KnownCapabilities.Contains(capability);

    public static HashSet<string>? RequiredCapabilities(
        ProjectInfo project, FactoryJob job, FactorySettings settings)
    {
        if (!BuilderExecutionProfile.Allows(project, job) || settings.Runtime is not ("exec" or "app-server"))
            return null;

        return RequiredCapabilitiesForRuntime(project, job, settings.Runtime);
    }

    private static HashSet<string>? RequiredCapabilitiesForRuntime(ProjectInfo project, FactoryJob job, string runtime)
    {
        if (!BuilderExecutionProfile.Allows(project, job) ||
            !BuilderExecutionProfile.MatchesRuntime(project, job, runtime)) return null;
        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "native-agent", "git-cli", "runtime:" + runtime
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

    // In-flight work remains allowed while draining, but not after a project
    // grant, worker identity or required capability evidence is revoked.
    public static bool CanContinue(FactoryState state, WorkerInfo worker,
        ProjectInfo project, FactoryJob job, FactorySettings settings)
    {
        if (!worker.Enabled || job.ProjectId != project.Id ||
            !CanAccessProject(state, worker.Id, project.Id))
            return false;

        var runtime = settings.Runtime;
        if (RemoteProviderRouting.Required(project, job))
        {
            var routes = RemoteProviderRouting.Plan(state, settings, job);
            if (routes is null) return false;
            runtime = routes.Single(route => route.Role == "builder").Runtime;
        }
        var required = runtime is "exec" or "app-server" ? RequiredCapabilitiesForRuntime(project, job, runtime) : null;
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

    public static bool CanExecute(FactoryState state, WorkerInfo worker,
        ProjectInfo project, FactoryJob job, FactorySettings settings) =>
        !worker.Draining && CanContinue(state, worker, project, job, settings);
}
