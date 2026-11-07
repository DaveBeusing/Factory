using Microsoft.Data.Sqlite;

namespace Factory;

internal sealed record ProjectProviderHold(Guid Id, Guid? JobId, Guid? GoalId, string ProviderId,
    string Model, long Reserved, long InputTokens, long OutputTokens);

public sealed partial class StateStore
{
    private static void UpgradeProjectBudgetReconciliation(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS budget_reconciler_project_grants(
                subject_id TEXT NOT NULL REFERENCES viewer_subjects(id),
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                PRIMARY KEY(subject_id, project_id)
            );
            CREATE TABLE IF NOT EXISTS project_budget_audit(
                id INTEGER PRIMARY KEY, subject_id TEXT NOT NULL, project_id TEXT NOT NULL,
                reservation_id TEXT NOT NULL, usage_id TEXT NOT NULL, reserved INTEGER NOT NULL,
                input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
                confirm_overrun INTEGER NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL
            );
            PRAGMA user_version=16;
            """);

    internal IReadOnlyList<ProjectProviderHold> ReadProjectProviderHolds(string authorization, Guid projectId)
    {
        var verifier = ReadViewerVerifier(authorization) ?? throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        RequireBudgetReconciler(db, tx, verifier, projectId);
        return ReadViewerRows(db, tx, projectId, """
            SELECT r.id,u.job_id,u.goal_id,u.provider_id,u.model,r.reserved,u.input_tokens,u.output_tokens
            FROM provider_budget_reservations r JOIN provider_usages u ON u.id=r.usage_id
            WHERE r.project_id=$project AND u.project_id=$project AND r.state='held'
              AND NOT EXISTS(SELECT 1 FROM remote_provider_turns t WHERE t.usage_id=u.id)
              AND (u.job_id IS NULL OR EXISTS(SELECT 1 FROM jobs j
                  WHERE j.id=u.job_id AND j.project_id=$project AND j.goal_id IS u.goal_id))
              AND (u.goal_id IS NULL OR EXISTS(SELECT 1 FROM goals g WHERE g.id=u.goal_id AND g.project_id=$project))
            ORDER BY r.position,r.id;
            """, row => new ProjectProviderHold(Guid.Parse(row.GetString(0)), ReadOptionalId(row, 1), ReadOptionalId(row, 2),
                row.GetString(3), row.GetString(4), row.GetInt64(5), row.GetInt64(6), row.GetInt64(7)));
    }

    internal object ReconcileProjectProviderHold(string authorization, Guid projectId, Guid reservationId,
        ProviderBudgetReconciliationInput input)
    {
        var verifier = ReadViewerVerifier(authorization) ?? throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        using var db = Open();
        using (var read = db.BeginTransaction(deferred: true))
            RequireBudgetReconciler(db, read, verifier, projectId, reservationId);
        using var tx = db.BeginTransaction();
        var actor = RequireBudgetReconciler(db, tx, verifier, projectId, reservationId);
        var result = ApplyProjectProviderSettlement(db, tx, actor, projectId, reservationId, input);
        tx.Commit();
        return result;
    }

    private static object ApplyProjectProviderSettlement(SqliteConnection db, SqliteTransaction tx, Guid actor,
        Guid projectId, Guid reservationId, ProviderBudgetReconciliationInput input,
        RemoteProviderTurn? turn = null, RemoteExecutorReceipt? receipt = null)
    {
        using var query = Command(db, tx, """
            SELECT r.usage_id,r.provider_id,r.reserved,r.state,u.job_id,u.goal_id,u.provider_id,u.execution_generation,
                u.input_tokens,u.output_tokens,
                EXISTS(SELECT 1 FROM remote_provider_turns t WHERE t.usage_id=u.id),
                u.goal_id IS NULL OR EXISTS(SELECT 1 FROM goals g WHERE g.id=u.goal_id AND g.project_id=$project)
            FROM provider_budget_reservations r JOIN provider_usages u ON u.id=r.usage_id
            WHERE r.id=$reservation AND r.project_id=$project AND u.project_id=$project;
            """, ("$reservation", reservationId), ("$project", projectId));
        using var row = query.ExecuteReader();
        if (!row.Read() || !row.GetBoolean(11)) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        if (row.GetBoolean(10) && turn is null)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Executor-backed turns require the executor recovery workflow.");
        var usage = new ProviderUsageEntry
        {
            Id = Guid.Parse(row.GetString(0)), ProjectId = projectId, JobId = ReadOptionalId(row, 4),
            GoalId = ReadOptionalId(row, 5), ProviderId = row.GetString(6), ExecutionGeneration = row.GetInt64(7),
            InputTokens = row.GetInt64(8), OutputTokens = row.GetInt64(9)
        };
        var reservation = new ProviderBudgetReservation
        {
            Id = reservationId, ProjectId = projectId, UsageId = usage.Id, ProviderId = row.GetString(1),
            Reserved = row.GetInt64(2), State = row.GetString(3)
        };
        row.Close();
        if (reservation.ProviderId != usage.ProviderId)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The provider reservation and usage do not match.");
        var state = new FactoryState { ProviderUsages = [usage], ProviderBudgetReservations = [reservation] };
        if (turn is not null) state.RemoteProviderTurns.Add(turn);
        FactoryJob? job = null;
        if (usage.JobId is { } jobId)
        {
            job = ReadJob(db, tx, jobId, projectId);
            if (job is null || job.GoalId != usage.GoalId) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
            state.Jobs.Add(job);
            if (usage.GoalId.HasValue) ReadJobBudgetReservations(db, tx, state, jobId);
        }

        object result;
        try { result = ProviderBudgetReconciliation.ApplyReconciliation(state, reservationId, input,
            receipt?.ObservedModel, receipt?.ReportedCostMicros); }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
        {
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict,
                ex is OverflowException ? "Token totals exceed the supported range." : ex.Message);
        }
        Execute(db, tx, """
            UPDATE provider_usages SET input_tokens=$input,output_tokens=$output,final_usage_reported=1,
                reported_cost_micros=COALESCE($cost,reported_cost_micros) WHERE id=$usage;
            UPDATE provider_budget_reservations SET consumed=$consumed,state=$state WHERE id=$reservation;
            """, ("$input", usage.InputTokens), ("$output", usage.OutputTokens), ("$usage", usage.Id),
            ("$consumed", reservation.Consumed), ("$state", reservation.State), ("$reservation", reservationId),
            ("$cost", receipt?.ReportedCostMicros is >= 0 ? receipt.ReportedCostMicros : null));
        if (turn is not null)
            Execute(db, tx, "UPDATE remote_provider_turns SET state=$state,observed_model=$model WHERE id=$turn;",
                ("$state", turn.State), ("$model", turn.ObservedModel), ("$turn", turn.Id));
        if (job is not null)
            Execute(db, tx, "UPDATE jobs SET tokens_in=$input,tokens_out=$output WHERE id=$job AND project_id=$project;",
                ("$input", job.TokensIn), ("$output", job.TokensOut), ("$job", job.Id), ("$project", projectId));
        var linked = state.BudgetReservations.SingleOrDefault(item =>
            item.GoalId == usage.GoalId && item.ExecutionGeneration == usage.ExecutionGeneration);
        if (linked is not null)
            Execute(db, tx, "UPDATE goal_budget_reservations SET consumed=$consumed,updated_at=$at WHERE id=$id;",
                ("$consumed", linked.Consumed), ("$at", linked.UpdatedAt), ("$id", linked.Id));
        RequireRepresentableBudgetTotals(db, tx, usage);
        Execute(db, tx, """
            INSERT INTO project_budget_audit(subject_id,project_id,reservation_id,usage_id,reserved,
                input_tokens,output_tokens,confirm_overrun,reason,at)
            VALUES($actor,$project,$reservation,$usage,$reserved,$input,$output,$overrun,$reason,$at);
            """, ("$actor", actor), ("$project", projectId), ("$reservation", reservationId), ("$usage", usage.Id),
            ("$reserved", reservation.Reserved), ("$input", usage.InputTokens), ("$output", usage.OutputTokens),
            ("$overrun", input.ConfirmOverrun), ("$reason", input.Reason.Trim()), ("$at", DateTimeOffset.UtcNow));
        var entry = state.Events.Single();
        AppendEvent(db, tx, entry.JobId, entry.Type, entry.Message);
        return result;
    }

    private static void RequireRepresentableBudgetTotals(SqliteConnection db, SqliteTransaction tx, ProviderUsageEntry usage)
    {
        // Use the transaction's proposed values and widen before adding, including outstanding capacity.
        using var provider = Command(db, tx, """
            SELECT input_tokens,output_tokens,0 FROM provider_usages WHERE provider_id=$provider
            UNION ALL
            SELECT reserved,consumed,1 FROM provider_budget_reservations
            WHERE provider_id=$provider AND state<>'released';
            """, ("$provider", usage.ProviderId));
        RequireRepresentableTotal(provider);
        if (usage.GoalId is { } goalId) RequireRepresentableGoalTotal(db, tx, goalId);
    }

    private static void RequireRepresentableGoalTotal(SqliteConnection db, SqliteTransaction tx, Guid goalId)
    {
        using var goal = Command(db, tx, """
            SELECT tokens_in,tokens_out,0 FROM jobs WHERE goal_id=$goal
            UNION ALL
            SELECT input_tokens,output_tokens,0 FROM provider_usages WHERE goal_id=$goal AND job_id IS NULL
            UNION ALL
            SELECT reserved,consumed,1 FROM goal_budget_reservations WHERE goal_id=$goal AND state IN ('active','held')
            UNION ALL
            SELECT r.reserved,r.consumed,1 FROM provider_budget_reservations r
            JOIN provider_usages u ON u.id=r.usage_id
            WHERE u.goal_id=$goal AND u.job_id IS NULL AND r.state IN ('active','held');
            """, ("$goal", goalId));
        RequireRepresentableTotal(goal);
    }

    private static void RequireRepresentableTotal(SqliteCommand command)
    {
        using var row = command.ExecuteReader();
        Int128 total = 0;
        while (row.Read())
        {
            Int128 first = row.GetInt64(0);
            Int128 second = row.GetInt64(1);
            total += row.GetBoolean(2) ? Int128.Max(0, first - second) : first + second;
            if (total > long.MaxValue)
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The settlement would exceed the supported budget total.");
        }
    }

    private static Guid RequireBudgetReconciler(SqliteConnection db, SqliteTransaction tx, string verifier,
        Guid projectId, Guid? reservationId = null)
    {
        var access = ReadProjectAccess(db, tx, verifier, projectId);
        if (access.SubjectId is not { } actor) throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        if (!access.CanRead) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        if (reservationId is { } id)
        {
            using var target = Command(db, tx, """
                SELECT 1 FROM provider_budget_reservations r JOIN provider_usages u ON u.id=r.usage_id
                WHERE r.id=$id AND r.project_id=$project AND u.project_id=$project;
                """, ("$id", id), ("$project", projectId));
            if (target.ExecuteScalar() is null) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        }
        if (!access.CanReconcileBudget) throw new ScopedProjectRejected(DraftGoalStatus.Forbidden);
        return actor;
    }

    private static Guid? ReadOptionalId(SqliteDataReader row, int column) =>
        row.IsDBNull(column) ? null : Guid.Parse(row.GetString(column));

    private static void ReadJobBudgetReservations(SqliteConnection db, SqliteTransaction tx, FactoryState state, Guid jobId)
    {
        using var query = Command(db, tx, """
            SELECT id,goal_id,execution_generation,base_usage,reserved,consumed,state,updated_at
            FROM goal_budget_reservations WHERE job_id=$job;
            """, ("$job", jobId));
        using var row = query.ExecuteReader();
        while (row.Read())
            state.BudgetReservations.Add(new()
            {
                Id = Guid.Parse(row.GetString(0)), GoalId = Guid.Parse(row.GetString(1)), JobId = jobId,
                ExecutionGeneration = row.GetInt64(2), BaseUsage = row.GetInt64(3), Reserved = row.GetInt64(4),
                Consumed = row.GetInt64(5), State = row.GetString(6),
                UpdatedAt = (DateTimeOffset)FromDb(row.GetString(7), typeof(DateTimeOffset))!
            });
    }
}
