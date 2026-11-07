using Microsoft.Data.Sqlite;

namespace Factory;

internal sealed record ProjectGoalHold(Guid Id, Guid GoalId, Guid JobId, long Reserved, long Consumed);
internal sealed record ProjectExecutorHold(Guid TurnId, Guid JobId, Guid ReservationId, string ProviderId, string Model);
internal sealed record ProjectExecutorRecovery(Guid TurnId, Guid ReservationId, string State);

public sealed partial class StateStore
{
    private sealed record ReceiptTarget(RemoteProviderTurn Turn, RemoteProviderLease Lease,
        FactoryJob Job, ProviderUsageEntry Usage, Guid ReservationId)
    {
        public ProjectExecutorRecovery RecoveryResult => new(Turn.Id, ReservationId, Turn.State);
    }

    private static void UpgradeProjectGoalReconciliation(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS project_goal_budget_audit(
                id INTEGER PRIMARY KEY, subject_id TEXT NOT NULL, project_id TEXT NOT NULL,
                goal_id TEXT NOT NULL, job_id TEXT NOT NULL, reservation_id TEXT NOT NULL,
                reserved INTEGER NOT NULL, input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
                confirm_overrun INTEGER NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL
            );
            PRAGMA user_version=17;
            """);

    internal IReadOnlyList<ProjectGoalHold> ReadProjectGoalHolds(string authorization, Guid projectId)
    {
        var verifier = RequireBudgetVerifier(authorization);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        RequireBudgetReconciler(db, tx, verifier, projectId);
        return ReadViewerRows(db, tx, projectId, """
            SELECT r.id,r.goal_id,r.job_id,r.reserved,r.consumed FROM goal_budget_reservations r
            JOIN goals g ON g.id=r.goal_id JOIN jobs j ON j.id=r.job_id AND j.goal_id=g.id
            WHERE g.project_id=$project AND j.project_id=$project AND r.state='held'
            ORDER BY r.position,r.id;
            """, row => new ProjectGoalHold(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)),
                Guid.Parse(row.GetString(2)), row.GetInt64(3), row.GetInt64(4)));
    }

    internal ProjectGoalHold ReconcileProjectGoalHold(string authorization, Guid projectId, Guid goalId,
        BudgetReconciliationInput input)
    {
        var verifier = RequireBudgetVerifier(authorization);
        using var db = Open();
        using (var read = db.BeginTransaction(deferred: true))
        {
            RequireBudgetReconciler(db, read, verifier, projectId);
            FindGoalHoldJob(db, read, projectId, goalId, input.ReservationId);
        }
        var reason = RequireBudgetReview(input.Reviewed, input.Reason);
        using var tx = db.BeginTransaction();
        var actor = RequireBudgetReconciler(db, tx, verifier, projectId);
        var jobId = FindGoalHoldJob(db, tx, projectId, goalId, input.ReservationId);
        var job = ReadJob(db, tx, jobId, projectId) ?? throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        var state = new FactoryState { Jobs = [job] };
        ReadJobBudgetReservations(db, tx, state, jobId);
        var target = state.BudgetReservations.Single(r => r.Id == input.ReservationId);
        using (var turns = Command(db, tx, """
            SELECT u.id,u.project_id,u.final_usage_reported,r.state,r.project_id
            FROM provider_usages u JOIN provider_budget_reservations r ON r.usage_id=u.id
            WHERE u.job_id=$job AND u.goal_id=$goal AND u.execution_generation=$generation;
            """, ("$job", jobId), ("$goal", goalId), ("$generation", target.ExecutionGeneration)))
        using (var row = turns.ExecuteReader())
        {
            while (row.Read())
            {
                if (Guid.Parse(row.GetString(1)) != projectId || Guid.Parse(row.GetString(4)) != projectId)
                    throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Linked provider accounting does not match the project.");
                var usageId = Guid.Parse(row.GetString(0));
                state.ProviderUsages.Add(new() { Id = usageId, JobId = jobId, GoalId = goalId,
                    ExecutionGeneration = target.ExecutionGeneration, FinalUsageReported = row.GetBoolean(2) });
                state.ProviderBudgetReservations.Add(new() { UsageId = usageId, State = row.GetString(3) });
            }
        }
        try { GoalBudgetLedger.ApplyGoalReconciliation(state, goalId, target.Id, input.FinalTokensIn,
            input.FinalTokensOut, reason, input.ConfirmOverrun); }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
        {
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict,
                ex is OverflowException ? "Token totals exceed the supported range." : ex.Message);
        }
        Execute(db, tx, """
            UPDATE jobs SET tokens_in=$input,tokens_out=$output,usage_complete=1 WHERE id=$job AND project_id=$project;
            UPDATE goal_budget_reservations SET consumed=$consumed,state=$state,updated_at=$at WHERE id=$reservation;
            """, ("$input", job.TokensIn), ("$output", job.TokensOut), ("$job", jobId), ("$project", projectId),
            ("$consumed", target.Consumed), ("$state", target.State), ("$at", target.UpdatedAt), ("$reservation", target.Id));
        RequireRepresentableGoalTotal(db, tx, goalId);
        Execute(db, tx, """
            INSERT INTO project_goal_budget_audit(subject_id,project_id,goal_id,job_id,reservation_id,reserved,
                input_tokens,output_tokens,confirm_overrun,reason,at)
            VALUES($actor,$project,$goal,$job,$reservation,$reserved,$input,$output,$overrun,$reason,$at);
            """, ("$actor", actor), ("$project", projectId), ("$goal", goalId), ("$job", jobId),
            ("$reservation", target.Id), ("$reserved", target.Reserved), ("$input", job.TokensIn), ("$output", job.TokensOut),
            ("$overrun", input.ConfirmOverrun), ("$reason", reason), ("$at", target.UpdatedAt));
        var entry = state.Events.Single();
        AppendEvent(db, tx, entry.JobId, entry.Type, entry.Message);
        tx.Commit();
        return new(target.Id, goalId, jobId, target.Reserved, target.Consumed);
    }

    internal async Task<ProjectExecutorRecovery> RecoverProjectExecutorReceiptAsync(string authorization, FactorySettings settings,
        Guid projectId, Guid jobId, Guid turnId, RemoteReceiptRecoveryInput input, CancellationToken ct)
    {
        var verifier = RequireBudgetVerifier(authorization);
        using var db = Open();
        ReceiptTarget snapshot;
        Guid actor;
        long grant;
        using (var read = db.BeginTransaction(deferred: true))
        {
            actor = RequireBudgetReconciler(db, read, verifier, projectId);
            snapshot = ReadReceiptTarget(db, read, projectId, jobId, turnId);
            grant = ReadBudgetGrantRevision(db, read, actor, projectId)
                ?? throw new ScopedProjectRejected(DraftGoalStatus.Forbidden);
        }
        var reason = RequireBudgetReview(input.Reviewed, input.Reason);
        if (snapshot.Turn.State == "reconciled") return snapshot.RecoveryResult;
        RequireRecoverable(snapshot);
        var credential = RemoteProviderRouting.ExecutorCredential(settings)!;
        var receipt = await RemoteProviderExecutor.FetchReceiptAsync(snapshot.Lease.ExecutorUrl, credential,
            new(turnId, snapshot.Turn.GrantDigest), ct)
            ?? throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The configured executor has no durable receipt.");
        ct.ThrowIfCancellationRequested();
        using (var read = db.BeginTransaction(deferred: true))
            VerifyGrant(read);
        using var tx = db.BeginTransaction();
        VerifyGrant(tx);
        var current = ReadReceiptTarget(db, tx, projectId, jobId, turnId);
        if (current.Turn.State == "reconciled") return current.RecoveryResult;
        RequireRecoverable(current);
        if (current.Lease.Id != snapshot.Lease.Id || current.Lease.ExecutorUrl != snapshot.Lease.ExecutorUrl ||
            current.Turn.GrantDigest != snapshot.Turn.GrantDigest || current.Usage.Id != snapshot.Usage.Id ||
            current.ReservationId != snapshot.ReservationId || current.Job.ExecutionGeneration != snapshot.Job.ExecutionGeneration ||
            RemoteProviderRouting.ExecutorCredential(settings) != credential)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Receipt recovery state changed. Review the turn again.");
        try { RemoteProviderRouting.VerifyRecoveredReceipt(current.Turn, current.Lease, current.Usage, receipt); }
        catch (InvalidOperationException ex) { throw new ScopedProjectRejected(DraftGoalStatus.Conflict, ex.Message); }
        ct.ThrowIfCancellationRequested();
        var settlement = RemoteProviderRouting.ReceiptSettlementInput(turnId, input with { Reason = reason }, receipt);
        ApplyProjectProviderSettlement(db, tx, actor, projectId, current.ReservationId, settlement, current.Turn, receipt);
        tx.Commit();
        return current.RecoveryResult;

        void VerifyGrant(SqliteTransaction transaction)
        {
            var currentActor = RequireBudgetReconciler(db, transaction, verifier, projectId);
            if (currentActor != actor || ReadBudgetGrantRevision(db, transaction, actor, projectId) != grant)
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The Budget Reconciler grant changed. Review the turn again.");
        }

        void RequireRecoverable(ReceiptTarget target)
        {
            if (!RemoteProviderRouting.CanRecoverReceipt(target.Turn, target.Lease, target.Job, settings))
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "No held receipt from the configured executor is available.");
        }
    }

    internal IReadOnlyList<ProjectExecutorHold> ReadProjectExecutorHolds(string authorization, Guid projectId)
    {
        var verifier = RequireBudgetVerifier(authorization);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        RequireBudgetReconciler(db, tx, verifier, projectId);
        return ReadViewerRows(db, tx, projectId, """
            SELECT t.id,j.id,r.id,u.provider_id,u.model FROM remote_provider_turns t
            JOIN remote_provider_leases l ON l.id=t.lease_id
            JOIN jobs j ON j.id=l.job_id
            JOIN provider_usages u ON u.id=t.usage_id AND u.job_id=j.id AND u.goal_id IS j.goal_id
                AND u.execution_generation=l.execution_generation AND u.role=t.role
            JOIN provider_budget_reservations r ON r.usage_id=u.id AND r.provider_id=u.provider_id
            WHERE j.project_id=$project AND u.project_id=$project AND r.project_id=$project
                AND t.state='held' AND r.state='held'
                AND (j.goal_id IS NULL OR EXISTS(SELECT 1 FROM goals WHERE id=j.goal_id AND project_id=$project))
            ORDER BY t.position,t.id;
            """, row => new ProjectExecutorHold(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)),
                Guid.Parse(row.GetString(2)), row.GetString(3), row.GetString(4)));
    }

    private static Guid FindGoalHoldJob(SqliteConnection db, SqliteTransaction tx, Guid projectId, Guid goalId, Guid reservationId)
    {
        using var query = Command(db, tx, """
            SELECT j.id FROM goal_budget_reservations r JOIN goals g ON g.id=r.goal_id
            JOIN jobs j ON j.id=r.job_id AND j.goal_id=g.id
            WHERE r.id=$reservation AND g.id=$goal AND g.project_id=$project AND j.project_id=$project;
            """, ("$reservation", reservationId), ("$goal", goalId), ("$project", projectId));
        return query.ExecuteScalar() is string id ? Guid.Parse(id) : throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
    }

    private static ReceiptTarget ReadReceiptTarget(SqliteConnection db, SqliteTransaction tx, Guid projectId, Guid jobId, Guid turnId)
    {
        var job = ReadJob(db, tx, jobId, projectId) ?? throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        using var query = Command(db, tx, """
            SELECT t.lease_id,t.usage_id,t.role,t.input_commit,t.observed_model,t.state,t.grant_digest,
                l.executor_url,u.provider_id,u.model,r.id
            FROM remote_provider_turns t JOIN remote_provider_leases l ON l.id=t.lease_id
            JOIN provider_usages u ON u.id=t.usage_id AND u.job_id=l.job_id
                AND u.execution_generation=l.execution_generation AND u.role=t.role
            JOIN provider_budget_reservations r ON r.usage_id=u.id AND r.provider_id=u.provider_id
            WHERE t.id=$turn AND l.job_id=$job AND u.project_id=$project AND r.project_id=$project
                AND u.goal_id IS $goal
                AND ($goal IS NULL OR EXISTS(SELECT 1 FROM goals WHERE id=$goal AND project_id=$project));
            """, ("$turn", turnId), ("$job", jobId), ("$project", projectId), ("$goal", job.GoalId));
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        var turn = new RemoteProviderTurn
        {
            Id = turnId, LeaseId = Guid.Parse(row.GetString(0)), UsageId = Guid.Parse(row.GetString(1)),
            Role = row.GetString(2), InputCommit = row.GetString(3), ObservedModel = row.GetString(4),
            State = row.GetString(5), GrantDigest = row.GetString(6)
        };
        var lease = new RemoteProviderLease { Id = turn.LeaseId, JobId = jobId, ExecutorUrl = row.GetString(7) };
        var usage = new ProviderUsageEntry { Id = turn.UsageId, ProviderId = row.GetString(8), Model = row.GetString(9) };
        var reservation = Guid.Parse(row.GetString(10));
        if (row.Read()) throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The turn has ambiguous provider reservations.");
        return new(turn, lease, job, usage, reservation);
    }

    private static long? ReadBudgetGrantRevision(SqliteConnection db, SqliteTransaction tx, Guid actor, Guid projectId)
    {
        using var query = Command(db, tx, """
            SELECT id FROM viewer_access_audit WHERE subject_id=$actor AND project_id=$project
              AND operation='grant_budget_reconciler' ORDER BY id DESC LIMIT 1;
            """, ("$actor", actor), ("$project", projectId));
        return query.ExecuteScalar() as long?;
    }

    private static string RequireBudgetVerifier(string authorization) =>
        ReadViewerVerifier(authorization) ?? throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);

    private static string RequireBudgetReview(bool reviewed, string reason)
    {
        var evidence = reason?.Trim() ?? "";
        if (!reviewed || evidence.Length is < 10 or > 1000)
            throw new ScopedProjectRejected(DraftGoalStatus.Invalid, "Confirm the review with a reason of 10 to 1000 characters.");
        return evidence;
    }
}
