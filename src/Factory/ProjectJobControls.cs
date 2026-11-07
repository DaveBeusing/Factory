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

internal sealed record ProjectJobControlResult(Guid Id, Guid ProjectId, string State, DateTimeOffset UpdatedAt,
    string? RequestedState = null);
internal sealed record ProjectJobOperation(Guid Id, string Title, string State, string Mode, string[] Actions,
    bool ReviewBeforeResume, string? RequestedState, string? CheckpointCommit);

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

    internal IReadOnlyList<ProjectJobOperation> ReadProjectJobOperations(string authorization, Guid projectId)
    {
        var verifier = ReadViewerVerifier(authorization) ?? throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var access = ReadProjectAccess(db, tx, verifier, projectId);
        if (access.SubjectId is null) throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        if (!access.CanRead) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        if (!access.CanOperate) throw new ScopedProjectRejected(DraftGoalStatus.Forbidden);
        var now = DateTimeOffset.UtcNow;
        return ReadViewerRows(db, tx, projectId, """
            SELECT j.id,j.title,j.state,j.mode,j.stage,j.started_at,j.lease_owner,j.lease_until,
                LENGTH(j.lease_token)>0,j.requested_state,j.checkpoint_stage,j.checkpoint_commit,p.status
            FROM jobs j JOIN projects p ON p.id=j.project_id WHERE j.project_id=$project
            ORDER BY j.position,j.id;
            """, row =>
        {
            var state = row.GetString(2);
            var mode = row.GetString(3);
            var stage = row.GetString(4);
            var owner = row.GetString(6);
            var expires = (DateTimeOffset?)FromDb(row.GetValue(7), typeof(DateTimeOffset?));
            var active = JobStates.Active.Contains(state);
            var requested = row.GetString(9);
            var actions = new List<string>();
            string? checkpoint = null;
            if (row.GetString(12) != "archived")
            {
                if (active)
                {
                    if (requested.Length == 0 && HasCurrentLocalLease(mode, owner, row.GetBoolean(8), expires, now))
                        actions.AddRange(["pause", "stop"]);
                }
                else if (owner.Length == 0)
                {
                    if (state is JobStates.Queued or JobStates.Paused or JobStates.Stopped)
                        actions.AddRange(["pause", "stop"]);
                    if ((state is JobStates.Paused or JobStates.Stopped) && stage is not ("scope" or "reconcile"))
                        actions.Add("resume");
                    if (state == JobStates.Blocked && stage == "reconcile" && mode == JobExecutionModes.Local &&
                        row.GetString(10) is "worktree" or "agent" && row.GetString(11).Length != 0)
                    {
                        actions.Add("reconcile");
                        checkpoint = row.GetString(11);
                    }
                }
            }
            return new ProjectJobOperation(Guid.Parse(row.GetString(0)), row.GetString(1), state, mode, actions.ToArray(),
                actions.Contains("resume") && !row.IsDBNull(5), (active ? requested : "") switch
                {
                    LocalJobControl.ProjectPauseRequest => JobStates.Paused,
                    LocalJobControl.ProjectStopRequest => JobStates.Stopped,
                    _ => null
                }, checkpoint);
        });
    }

    private static bool HasCurrentLocalLease(string mode, string owner, bool hasToken,
        DateTimeOffset? expires, DateTimeOffset now) =>
        mode == JobExecutionModes.Local && !string.IsNullOrWhiteSpace(owner) &&
        !owner.StartsWith("remote:", StringComparison.Ordinal) && hasToken && expires > now;

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
            SELECT j.state, j.stage, j.lease_owner, j.started_at, j.error, j.requested_state, p.status,
                j.mode, j.lease_until, j.lease_token, j.updated_at
            FROM jobs j JOIN projects p ON p.id=j.project_id WHERE j.project_id=$project AND j.id=$job;
            """, ("$project", projectId), ("$job", jobId));
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        var previousState = row.GetString(0);
        if (row.GetString(6) == "archived")
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The job belongs to an archived project.");
        var leaseOwner = row.GetString(2);
        if (JobStates.Active.Contains(previousState))
        {
            var leaseUntil = (DateTimeOffset?)FromDb(row.GetValue(8), typeof(DateTimeOffset?));
            if (request.Action == "resume" ||
                !HasCurrentLocalLease(row.GetString(7), leaseOwner, row.GetString(9).Length > 0, leaseUntil, DateTimeOffset.UtcNow))
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Only a running local job with a current lease can be paused or stopped.");
            var pending = row.GetString(5);
            var updatedAt = (DateTimeOffset)FromDb(row.GetValue(10), typeof(DateTimeOffset))!;
            row.Close();
            var stop = request.Action == "stop";
            var signal = stop ? LocalJobControl.ProjectStopRequest : LocalJobControl.ProjectPauseRequest;
            var target = stop ? JobStates.Stopped : JobStates.Paused;
            if (pending == signal) return new(jobId, projectId, previousState, updatedAt, target);
            if (pending.Length != 0)
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The current lease already has a control request.");
            var now = DateTimeOffset.UtcNow;
            Execute(db, tx, """
                UPDATE jobs SET requested_state=$requested,updated_at=$at WHERE project_id=$project AND id=$job;
                """, ("$requested", signal), ("$at", now), ("$project", projectId), ("$job", jobId));
            AuditJobOperation(db, tx, actor, projectId, jobId, request.Action + "_requested",
                previousState, previousState, request.Reviewed, reason, now);
            AppendEvent(db, tx, jobId, "job_control_requested", request.Action + " " + reason);
            tx.Commit();
            return new(jobId, projectId, previousState, now, target);
        }
        if (leaseOwner.Length != 0)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The job still has a lease.");
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
        if (request.Action == "resume") RequireSettledJobProviderUsage(db, tx, jobId);
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
        AuditJobOperation(db, tx, actor, projectId, jobId, request.Action, previousState, job.State,
            request.Reviewed, reason, job.UpdatedAt);
        AppendEvent(db, tx, jobId, "job_control", request.Action + " " + reason);
        tx.Commit();
        return new(jobId, projectId, job.State, job.UpdatedAt);
    }

    private static void RequireSettledJobProviderUsage(SqliteConnection db, SqliteTransaction tx, Guid jobId)
    {
        using var unsettled = Command(db, tx, """
            SELECT 1 FROM provider_budget_reservations r JOIN provider_usages u ON u.id=r.usage_id
            WHERE u.job_id=$job AND r.state IN ('active','held') LIMIT 1;
            """, ("$job", jobId));
        if (unsettled.ExecuteScalar() is not null)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Settle outstanding provider reservations before requeueing this job.");
    }

    internal ProjectJobControlResult ReconcileProjectJob(string authorization, Guid projectId, Guid jobId,
        ProjectJobControlInput request)
    {
        var verifier = ReadViewerVerifier(authorization)
            ?? throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        using var db = Open();
        FactoryJob snapshot;
        Guid actor;
        long grant;
        using (var read = db.BeginTransaction(deferred: true))
        {
            actor = CheckJobControlAccess(db, read, verifier, projectId, jobId, request);
            grant = ReadOperatorGrantRevision(db, read, actor, projectId)
                ?? throw new ScopedProjectRejected(DraftGoalStatus.Forbidden);
            snapshot = ReadRecoveryTarget(db, read, projectId, jobId);
        }
        var reason = request.Reason?.Trim() ?? "";
        if (!request.Reviewed || reason.Length is < 10 or > 1000)
            throw new ScopedProjectRejected(DraftGoalStatus.Invalid, "Confirm the process and worktree review with a reason of 10 to 1000 characters.");
        VerifyEvidence(snapshot);

        using (var read = db.BeginTransaction(deferred: true))
            VerifyGrant(read);
        using var tx = db.BeginTransaction();
        VerifyGrant(tx);
        var current = ReadRecoveryTarget(db, tx, projectId, jobId);
        try { JobRecovery.VerifyRecoverySnapshot(snapshot, current); }
        catch (InvalidOperationException)
        {
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Recovery state changed. Review the job again.");
        }
        RequireSettledJobProviderUsage(db, tx, jobId);
        VerifyEvidence(current);
        var now = DateTimeOffset.UtcNow;
        Execute(db, tx, """
            UPDATE jobs SET state='queued',stage='reconciled',error='',requested_state='',updated_at=$at
            WHERE project_id=$project AND id=$job;
            """, ("$at", now), ("$project", projectId), ("$job", jobId));
        AuditJobOperation(db, tx, actor, projectId, jobId, "reconcile", current.State, JobStates.Queued, true, reason, now);
        AppendEvent(db, tx, jobId, "reconciled", "Reviewed clean checkpoint " + current.CheckpointCommit + "; " + reason);
        tx.Commit();
        return new(jobId, projectId, JobStates.Queued, now);

        void VerifyGrant(SqliteTransaction transaction)
        {
            var currentActor = CheckJobControlAccess(db, transaction, verifier, projectId, jobId, request);
            if (currentActor != actor || ReadOperatorGrantRevision(db, transaction, actor, projectId) != grant)
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The Operator grant changed. Review the job again.");
        }

        static void VerifyEvidence(FactoryJob job)
        {
            try { JobRecovery.VerifyLocalRecoveryEvidence(job); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Recovery requires a stopped local runner and a clean, matching checkpoint.");
            }
        }
    }

    private static FactoryJob ReadRecoveryTarget(SqliteConnection db, SqliteTransaction tx, Guid projectId, Guid jobId)
    {
        using var project = Command(db, tx, "SELECT status FROM projects WHERE id=$project;", ("$project", projectId));
        if (project.ExecuteScalar() is "archived")
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The job belongs to an archived project.");
        return ReadJob(db, tx, jobId, projectId) ?? throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
    }

    private static long? ReadOperatorGrantRevision(SqliteConnection db, SqliteTransaction tx, Guid actor, Guid projectId)
    {
        using var query = Command(db, tx, """
            SELECT id FROM viewer_access_audit WHERE subject_id=$subject AND project_id=$project
              AND operation='grant_operator' ORDER BY id DESC LIMIT 1;
            """, ("$subject", actor), ("$project", projectId));
        return query.ExecuteScalar() as long?;
    }

    private static void AuditJobOperation(SqliteConnection db, SqliteTransaction tx, Guid actor, Guid projectId,
        Guid jobId, string operation, string previousState, string state, bool reviewed, string reason, DateTimeOffset at) =>
        Execute(db, tx, """
            INSERT INTO project_operation_audit(subject_id,project_id,job_id,operation,previous_state,
                resulting_state,reviewed,reason,at)
            VALUES($actor,$project,$job,$operation,$previous,$state,$reviewed,$reason,$at);
            """, ("$actor", actor), ("$project", projectId), ("$job", jobId), ("$operation", operation),
            ("$previous", previousState), ("$state", state), ("$reviewed", reviewed),
            ("$reason", reason), ("$at", at));

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
