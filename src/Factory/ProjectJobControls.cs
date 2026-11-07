using Microsoft.Data.Sqlite;

namespace Factory;

public sealed class ProjectJobControlInput
{
    public Guid? ProjectId { get; set; }
    public Guid? JobId { get; set; }
    public string Action { get; set; } = "";
    public bool Reviewed { get; set; }
    public string Reason { get; set; } = "";
}

internal sealed record ProjectJobControlResult(Guid Id, Guid ProjectId, string State, DateTimeOffset UpdatedAt);

public sealed partial class StateStore
{
    private static void UpgradeProjectControls(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS operator_project_grants(
                subject_id TEXT NOT NULL REFERENCES viewer_subjects(id),
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                PRIMARY KEY(subject_id, project_id)
            );
            CREATE TABLE IF NOT EXISTS project_operation_audit(
                id INTEGER PRIMARY KEY, subject_id TEXT NOT NULL, project_id TEXT NOT NULL,
                job_id TEXT NOT NULL, operation TEXT NOT NULL, previous_state TEXT NOT NULL,
                resulting_state TEXT NOT NULL, reviewed INTEGER NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL
            );
            PRAGMA user_version=15;
            """);

    internal ProjectJobControlResult ControlProjectJob(string authorization, Guid projectId, Guid jobId,
        ProjectJobControlInput request)
    {
        var verifier = ReadViewerVerifier(authorization)
            ?? throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        using var db = Open();
        using (var read = db.BeginTransaction(deferred: true))
            CheckJobControlAccess(db, read, verifier, projectId, jobId, request);

        if (request.Action is not ("pause" or "resume" or "stop"))
            throw new ScopedProjectRejected(DraftGoalStatus.Invalid, "Choose pause, resume or stop.");
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length > 2000)
            throw new ScopedProjectRejected(DraftGoalStatus.Invalid, "The reason may contain at most 2000 characters.");

        using var tx = db.BeginTransaction();
        var actor = CheckJobControlAccess(db, tx, verifier, projectId, jobId, request);
        using var query = Command(db, tx, """
            SELECT j.state, j.stage, j.lease_owner, j.started_at, j.error, j.requested_state, p.status
            FROM jobs j JOIN projects p ON p.id=j.project_id WHERE j.project_id=$project AND j.id=$job;
            """, ("$project", projectId), ("$job", jobId));
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        var previousState = row.GetString(0);
        if (row.GetString(6) == "archived" || JobStates.Active.Contains(previousState) || row.GetString(2).Length != 0)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The job is leased, executing or belongs to an archived project.");
        if (request.Action == "resume" && previousState is not (JobStates.Paused or JobStates.Stopped))
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Only paused or stopped jobs can be resumed here.");
        var job = new FactoryJob
        {
            Id = jobId, ProjectId = projectId, State = previousState, Stage = row.GetString(1),
            StartedAt = (DateTimeOffset?)FromDb(row.GetValue(3), typeof(DateTimeOffset?)),
            Error = row.GetString(4), RequestedState = row.GetString(5)
        };
        row.Close();
        if (request.Action == "resume" && job.StartedAt.HasValue && reason.Length == 0)
            throw new ScopedProjectRejected(DraftGoalStatus.Invalid, "Record what was reviewed before resuming this job.");
        if (request.Action == "resume")
        {
            using var unsettled = Command(db, tx, """
                SELECT 1 FROM provider_budget_reservations r JOIN provider_usages u ON u.id=r.usage_id
                WHERE u.job_id=$job AND r.state IN ('active','held') LIMIT 1;
                """, ("$job", jobId));
            if (unsettled.ExecuteScalar() is not null)
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Settle outstanding provider reservations before resuming this job.");
        }
        try
        {
            MissionControlManager.ApplyInactiveJobControl(job,
                new ControlInput { Action = request.Action, Reviewed = request.Reviewed, Reason = reason });
        }
        catch (InvalidOperationException ex)
        {
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, ex.Message);
        }
        Execute(db, tx, """
            UPDATE jobs SET state=$state,error=$error,requested_state=$requested,updated_at=$at
            WHERE project_id=$project AND id=$job;
            """, ("$state", job.State), ("$error", job.Error), ("$requested", job.RequestedState),
            ("$at", job.UpdatedAt), ("$project", projectId), ("$job", jobId));
        Execute(db, tx, """
            INSERT INTO project_operation_audit(subject_id,project_id,job_id,operation,previous_state,
                resulting_state,reviewed,reason,at)
            VALUES($actor,$project,$job,$operation,$previous,$state,$reviewed,$reason,$at);
            """, ("$actor", actor), ("$project", projectId), ("$job", jobId), ("$operation", request.Action),
            ("$previous", previousState), ("$state", job.State), ("$reviewed", request.Reviewed),
            ("$reason", reason), ("$at", job.UpdatedAt));
        AppendEvent(db, tx, jobId, "job_control", request.Action + " " + reason);
        tx.Commit();
        return new(jobId, projectId, job.State, job.UpdatedAt);
    }

    private static Guid CheckJobControlAccess(SqliteConnection db, SqliteTransaction tx, string verifier,
        Guid projectId, Guid jobId, ProjectJobControlInput request)
    {
        var access = ReadProjectAccess(db, tx, verifier, projectId);
        if (access.SubjectId is not { } actor) throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        if (!access.CanRead || (request.ProjectId is { } project && project != projectId) ||
            (request.JobId is { } job && job != jobId))
            throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        using var target = Command(db, tx, "SELECT 1 FROM jobs WHERE project_id=$project AND id=$job;",
            ("$project", projectId), ("$job", jobId));
        if (target.ExecuteScalar() is null) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        if (!access.CanOperate) throw new ScopedProjectRejected(DraftGoalStatus.Forbidden);
        return actor;
    }
}
