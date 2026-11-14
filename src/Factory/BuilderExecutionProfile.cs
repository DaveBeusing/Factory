using Microsoft.Data.Sqlite;

namespace Factory;

internal static class BuilderExecutionProfile
{
    internal const string Filesystem = "isolation:filesystem";
    internal const string Network = "isolation:network";
    internal const string Process = "isolation:process";
    internal const string Secrets = "isolation:secrets";

    internal static bool IsRestriction(string? value) =>
        value is Filesystem or Network or Process or Secrets;

    // Existing adapters cannot attest the requested OS-level restrictions. A
    // worktree, a worker's assertion, and credential stripping are insufficient.
    internal static bool MatchesRuntime(ProjectInfo project, FactoryJob job, string runtime) =>
        project.RequiredExecutionCapabilities.Concat(job.RequiredExecutionCapabilities)
            .Where(c => c.StartsWith("runtime:", StringComparison.Ordinal))
            .All(c => c == "runtime:" + runtime);

    internal static bool AllowsLocal(ProjectInfo project, FactoryJob job, string runtime,
        bool customRuntime)
    {
        if (!Allows(project, job)) return false;
        return project.RequiredExecutionCapabilities.Concat(job.RequiredExecutionCapabilities).All(capability =>
            capability switch
            {
                "git-cli" => true,
                "native-checks" => project.Checks.Any(check => string.IsNullOrWhiteSpace(check.ContainerImage)),
                "container-checks" => project.Checks.Any(check => !string.IsNullOrWhiteSpace(check.ContainerImage)),
                _ => !customRuntime && (capability == "native-agent" || capability == "runtime:" + runtime)
            });
    }

    internal static bool Allows(ProjectInfo project, FactoryJob job) =>
        !project.RequiredExecutionCapabilities.Concat(job.RequiredExecutionCapabilities)
            .Any(requirement => IsRestriction(requirement) || !WorkerAuthorization.IsKnownCapability(requirement));
}

public sealed partial class StateStore
{
    private static void UpgradeExecutionProfiles(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS execution_provenance(
                job_id TEXT NOT NULL, project_id TEXT NOT NULL, generation INTEGER NOT NULL,
                worker TEXT NOT NULL, mode TEXT NOT NULL, runtime TEXT NOT NULL,
                isolation_assurance TEXT NOT NULL, requested_capabilities TEXT NOT NULL,
                worker_capability_evidence TEXT NOT NULL, recorded_at TEXT NOT NULL,
                PRIMARY KEY(job_id,generation)
            );
            CREATE TABLE IF NOT EXISTS consumed_worker_claims(
                job_id TEXT PRIMARY KEY, generation INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS started_builder_claims(
                job_id TEXT PRIMARY KEY, generation INTEGER NOT NULL
            );
            PRAGMA user_version=23;
            """);

    internal bool ConsumeWorkerClaim(Guid job, long generation)
    {
        if (job == Guid.Empty || generation <= 0) return false;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var command = Command(db, tx, """
            INSERT INTO consumed_worker_claims(job_id,generation) VALUES($job,$generation)
            ON CONFLICT(job_id) DO UPDATE SET generation=excluded.generation
            WHERE excluded.generation>consumed_worker_claims.generation;
            """, ("$job", job), ("$generation", generation));
        var accepted = command.ExecuteNonQuery() == 1;
        tx.Commit();
        return accepted;
    }

    internal bool StartBuilderClaim(Guid job, ExecutionFence fence)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var current = ReadJob(db, tx, job);
        if (current is null || !JobRecovery.Matches(current, fence)) return false;
        var accepted = TryRecordBuilderStart(db, tx, job, fence.Generation);
        tx.Commit();
        return accepted;
    }

    internal bool StartRemoteBuilder(WorkerStartRequest request, FactorySettings settings) =>
        Change((state, db, tx) =>
        {
            var job = state.Jobs.SingleOrDefault(j => j.Id == request.JobId && j.ProjectId == request.ProjectId);
            var worker = state.Workers.SingleOrDefault(w => w.Id == request.WorkerId);
            var project = state.Projects.SingleOrDefault(p => p.Id == request.ProjectId);
            if (job is null || worker is null || project is null || job.Mode != JobExecutionModes.Remote ||
                !JobRecovery.Matches(job, new(request.ExecutionGeneration, request.LeaseToken, "remote:" + worker.Id)) ||
                !WorkerAuthorization.CanContinue(state, worker, project, job, settings))
                return false;
            var route = RemoteProviderRouting.Current(state, job);
            if (route?.Id != request.ProviderLeaseId ||
                (RemoteProviderRouting.Required(project, job) && route is null) ||
                (route is not null && !RemoteProviderRouting.CanContinue(state, settings, job, route)))
                return false;
            return TryRecordBuilderStart(db, tx, job.Id, job.ExecutionGeneration);
        });

    private static bool TryRecordBuilderStart(SqliteConnection db, SqliteTransaction tx, Guid job, long generation)
    {
        using var command = Command(db, tx, """
            INSERT INTO started_builder_claims(job_id,generation) VALUES($job,$generation)
            ON CONFLICT(job_id) DO UPDATE SET generation=excluded.generation
            WHERE excluded.generation>started_builder_claims.generation;
            """, ("$job", job), ("$generation", generation));
        return command.ExecuteNonQuery() == 1;
    }

    internal static void RecordExecutionProvenance(SqliteConnection db, SqliteTransaction tx, ProjectInfo project,
        FactoryJob job, string runtime, IEnumerable<WorkerCapabilityProof>? evidence = null)
    {
        var requested = project.RequiredExecutionCapabilities.Concat(job.RequiredExecutionCapabilities)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Execute(db, tx, """
            INSERT INTO execution_provenance(job_id,project_id,generation,worker,mode,runtime,
                isolation_assurance,requested_capabilities,worker_capability_evidence,recorded_at)
            VALUES($job,$project,$generation,$worker,$mode,$runtime,'unverified',$requested,$evidence,$at);
            """, ("$job", job.Id), ("$project", project.Id), ("$generation", job.ExecutionGeneration),
            ("$worker", job.LeaseOwner), ("$mode", job.Mode), ("$runtime", runtime),
            ("$requested", Json.Stringify(requested)),
            ("$evidence", Json.Stringify(evidence?.OrderBy(p => p.Capability, StringComparer.Ordinal)
                .Select(p => new { p.Capability, p.VerifiedAt }).ToArray())),
            ("$at", DateTimeOffset.UtcNow));
    }
}
