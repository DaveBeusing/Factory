using Microsoft.Data.Sqlite;

namespace Factory;

public sealed partial class StateStore
{
    // A null result delegates fencing and audit side effects to the aggregate path.
    // That path rechecks the lease and authorization in its own write transaction.
    internal bool? TryRefreshRemoteHeartbeat(WorkerStatus request, FactorySettings settings)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var job = ReadJob(db, tx, request.JobId);
        var fence = new ExecutionFence(request.ExecutionGeneration, request.LeaseToken, "remote:" + request.WorkerId);
        if (job is null || !JobRecovery.Matches(job, fence)) return false;
        using var started = Command(db, tx, """
            SELECT 1 FROM started_builder_claims WHERE job_id=$job AND generation=$generation;
            """, ("$job", job.Id), ("$generation", fence.Generation));
        if (started.ExecuteScalar() is null) return false;

        var workerKey = ("$worker", (object?)request.WorkerId);
        var projectKey = ("$project", (object?)job.ProjectId);
        var state = new FactoryState
        {
            Workers = ReadHeartbeatRows<WorkerInfo>(db, tx, "id=$worker", workerKey),
            Projects = ReadHeartbeatRows<ProjectInfo>(db, tx, "id=$project", projectKey),
            WorkerProjectGrants = ReadHeartbeatRows<WorkerProjectGrant>(db, tx,
                "worker_id=$worker AND project_id=$project", workerKey, projectKey),
            WorkerCapabilityProofs = ReadHeartbeatRows<WorkerCapabilityProof>(db, tx, "worker_id=$worker", workerKey)
        };
        var worker = state.Workers.SingleOrDefault();
        var project = state.Projects.SingleOrDefault();
        if (worker is null || !worker.Enabled || project is null) return false;
        project.RequiredExecutionCapabilities = ReadHeartbeatValues(db, tx,
            "projects_required_execution_capabilities", projectKey);
        LoadChecks(db, tx, new() { [Key(project)] = project }, project.Id);

        var providersRequired = RemoteProviderRouting.Required(project, job);
        var providerLease = ReadHeartbeatRows<RemoteProviderLease>(db, tx,
            "job_id=$job AND execution_generation=$generation", ("$job", job.Id), ("$generation", fence.Generation))
            .SingleOrDefault();
        if (providersRequired || providerLease is not null)
        {
            state.ProviderAssignments = ReadHeartbeatRows<ProviderAssignment>(db, tx, "project_id=$project", projectKey);
            state.ProviderBudgetLimits = ReadHeartbeatRows<ProviderBudgetLimit>(db, tx, "project_id=$project", projectKey);
        }
        if (!WorkerAuthorization.CanContinue(state, worker, project, job, settings)) return false;
        var now = DateTimeOffset.UtcNow;
        if (!JobRecovery.Matches(job, fence)) return false;
        if (providerLease is not null)
        {
            if (request.ProviderLeaseId != providerLease.Id ||
                !RemoteProviderRouting.CanContinue(state, settings, job, providerLease)) return null;
        }
        else
        {
            if (request.TokensIn < 0 || request.TokensOut < 0) return false;
            if (job.GoalId is not null)
                state.BudgetReservations = ReadHeartbeatRows<GoalBudgetReservation>(db, tx,
                    "job_id=$job AND execution_generation=$generation", ("$job", job.Id), ("$generation", fence.Generation));
            if (GoalBudgetLedger.Record(state, job, request.TokensIn, request.TokensOut, request.UsageComplete) < 0) return null;
            if (job.GoalId is not null)
            {
                var reservation = GoalBudgetLedger.Current(state, job);
                Execute(db, tx, "UPDATE goal_budget_reservations SET consumed=$used,updated_at=$at WHERE id=$id;",
                    ("$used", reservation.Consumed), ("$at", reservation.UpdatedAt), ("$id", reservation.Id));
            }
        }
        Execute(db, tx, "UPDATE workers SET heartbeat=$now WHERE id=$worker;", ("$now", now), workerKey);
        Execute(db, tx, """
            UPDATE jobs SET lease_until=$until,tokens_in=$input,tokens_out=$output,usage_complete=$complete
            WHERE id=$job;
            """, ("$until", now.AddSeconds(settings.LeaseSeconds)), ("$input", job.TokensIn),
            ("$output", job.TokensOut), ("$complete", job.UsageComplete), ("$job", job.Id));
        tx.Commit();
        return true;
    }

    private static List<T> ReadHeartbeatRows<T>(SqliteConnection db, SqliteTransaction tx, string where,
        params (string, object?)[] args) where T : new()
    {
        var map = Entities.Single(entry => entry.Type == typeof(T));
        var columns = Scalars(map);
        using var query = Command(db, tx, "SELECT " + string.Join(",", columns.Select(p => Q(Column(p.Name)))) +
            " FROM " + Q(map.Table) + " WHERE " + where + " ORDER BY position,id;", args);
        using var reader = query.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            var row = new T();
            for (var index = 0; index < columns.Length; index++)
                columns[index].SetValue(row, FromDb(reader.GetValue(index), columns[index].PropertyType));
            rows.Add(row);
        }
        return rows;
    }

    private static List<string> ReadHeartbeatValues(SqliteConnection db, SqliteTransaction tx, string table,
        (string, object?) project)
    {
        using var query = Command(db, tx, "SELECT value FROM " + Q(table) + " WHERE owner_id=$project ORDER BY position;", project);
        using var reader = query.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }
}
