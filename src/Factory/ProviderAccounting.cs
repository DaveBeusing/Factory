using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Factory;

public sealed record ProviderTurnAllocation(Guid Id, long ReservedTokens, bool Limited);

public sealed class ProviderUsageBreakdown
{
    public string Model { get; set; } = "";
    public string Role { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long? ReportedCostMicros { get; set; }
}

public sealed class ProviderCapacity
{
    public Guid ProjectId { get; set; }
    public string ProviderId { get; set; } = "";
    public long? TokenLimit { get; set; }
    public long ConsumedTokens { get; set; }
    public long OutstandingTokens { get; set; }
    public long? RemainingTokens => TokenLimit.HasValue
        ? TokenLimit.Value - ConsumedTokens - OutstandingTokens : null;
    // Only totals the provider's own reported cost; never inferred from token prices.
    public long? ReportedCostMicros { get; set; }
    public List<ProviderUsageBreakdown> Breakdown { get; set; } = [];
}

/// <summary>
/// Provider usage and hard per-project provider reservations share the controller SQLite
/// transaction boundary. Account-level quotas remain separate provider observations.
/// </summary>
public sealed partial class StateStore
{
    private static void CreateProviderTables(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS project_provider_routes(
                project_id TEXT NOT NULL REFERENCES projects(id),
                role TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                model TEXT NOT NULL,
                PRIMARY KEY(project_id, role)
            );
            CREATE TABLE IF NOT EXISTS provider_budget_limits(
                project_id TEXT NOT NULL REFERENCES projects(id),
                provider_id TEXT NOT NULL,
                token_limit INTEGER NOT NULL CHECK(token_limit >= 0),
                PRIMARY KEY(project_id, provider_id)
            );
            CREATE TABLE IF NOT EXISTS provider_turns(
                id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id),
                job_id TEXT REFERENCES jobs(id),
                execution_generation INTEGER NOT NULL,
                provider_id TEXT NOT NULL,
                model TEXT NOT NULL,
                role TEXT NOT NULL,
                reserved_tokens INTEGER NOT NULL CHECK(reserved_tokens >= 0),
                consumed_input INTEGER NOT NULL DEFAULT 0 CHECK(consumed_input >= 0),
                consumed_output INTEGER NOT NULL DEFAULT 0 CHECK(consumed_output >= 0),
                limited INTEGER NOT NULL CHECK(limited IN (0,1)),
                state TEXT NOT NULL CHECK(state IN ('active','held','settled')),
                reported_cost_micros INTEGER CHECK(reported_cost_micros >= 0),
                created_at TEXT NOT NULL,
                finished_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_provider_turns_budget ON provider_turns(project_id,provider_id,state);
            CREATE INDEX IF NOT EXISTS idx_provider_turns_job ON provider_turns(job_id);
            PRAGMA user_version=7;
            """);
    }

    public IReadOnlyList<ProjectProviderRoute> GetProviderRoutes(Guid projectId)
    {
        using var db = Open();
        using var cmd = Command(db, null, """
            SELECT role,provider_id,model FROM project_provider_routes
            WHERE project_id=$project ORDER BY role;
            """, ("$project", projectId));
        using var reader = cmd.ExecuteReader();
        var routes = new List<ProjectProviderRoute>();
        while (reader.Read())
            routes.Add(new ProjectProviderRoute
            {
                ProjectId = projectId, Role = reader.GetString(0),
                ProviderId = reader.GetString(1), Model = reader.GetString(2)
            });
        return routes;
    }

    public void ReplaceProviderRoutes(Guid projectId, IReadOnlyList<ProjectProviderRoute> routes)
    {
        if (routes.Count > 8) throw new ArgumentException("Too many provider routes");
        if (routes.Any(r => r.ProjectId != projectId))
            throw new ArgumentException("Provider route project ID mismatch");
        if (routes.Select(r => r.Role).Distinct(StringComparer.Ordinal).Count() != routes.Count)
            throw new ArgumentException("Duplicate provider role");
        foreach (var route in routes)
            ProviderRouting.Validate(route.Role, route.ProviderId, route.Model);
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var check = Command(db, tx, "SELECT COUNT(*) FROM projects WHERE id=$id", ("$id", projectId)))
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new ArgumentException("Unknown project");
        Execute(db, tx, "DELETE FROM project_provider_routes WHERE project_id=$id", ("$id", projectId));
        foreach (var route in routes)
            Execute(db, tx, """
                INSERT INTO project_provider_routes(project_id,role,provider_id,model)
                VALUES($project,$role,$provider,$model);
                """, ("$project", projectId), ("$role", route.Role),
                ("$provider", route.ProviderId), ("$model", route.Model));
        tx.Commit();
        Event(null, "provider_routes_updated", "Project " + projectId + " provider routing updated");
    }

    public void SetProviderLimit(Guid projectId, string providerId, long tokenLimit)
    {
        if (!ProviderRouting.Providers.Contains(providerId, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported provider");
        if (tokenLimit < 0) throw new ArgumentOutOfRangeException(nameof(tokenLimit));
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var check = Command(db, tx, "SELECT COUNT(*) FROM projects WHERE id=$id", ("$id", projectId)))
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new ArgumentException("Unknown project");
        Execute(db, tx, """
            INSERT INTO provider_budget_limits(project_id,provider_id,token_limit)
            VALUES($project,$provider,$limit)
            ON CONFLICT(project_id,provider_id) DO UPDATE SET token_limit=excluded.token_limit;
            """, ("$project", projectId), ("$provider", providerId), ("$limit", tokenLimit));
        tx.Commit();
        Event(null, "provider_budget_updated", "Project " + projectId + " provider " + providerId + " limit updated");
    }

    public ProviderTurnAllocation BeginProviderTurn(Guid projectId, Guid? jobId,
        ExecutionFence? fence, SelectedProvider provider, int reservationTokens)
    {
        ProviderRouting.Validate(provider.Role, provider.ProviderId, provider.Model);
        if (reservationTokens < BudgetedAgentRuntime.MinimumTurnAllowance)
            throw new ArgumentOutOfRangeException(nameof(reservationTokens));
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (jobId.HasValue)
        {
            if (fence is null) throw new InvalidOperationException("Provider-funded job requires a fenced claim");
            using var check = Command(db, tx, """
                SELECT COUNT(*) FROM jobs WHERE id=$id AND project_id=$project
                AND execution_generation=$generation AND lease_token=$token
                AND lease_owner=$owner AND lease_until>$now
                AND state IN ('preparing','building','gating','reviewing','reworking','awaiting_approval');
                """, ("$id", jobId.Value), ("$project", projectId),
                ("$generation", fence.Value.Generation), ("$token", fence.Value.Token),
                ("$owner", fence.Value.Owner), ("$now", DateTimeOffset.UtcNow));
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new LostExecutionLeaseException("Provider reservation requires a live job lease");
        }
        else
        {
            using var check = Command(db, tx, "SELECT COUNT(*) FROM projects WHERE id=$id", ("$id", projectId));
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new ArgumentException("Unknown project");
        }
        long limit = 0;
        using (var q = Command(db, tx, """
            SELECT token_limit FROM provider_budget_limits WHERE project_id=$project AND provider_id=$provider
            """, ("$project", projectId), ("$provider", provider.ProviderId)))
        {
            var value = q.ExecuteScalar();
            if (value is not null && value is not DBNull)
                limit = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        long reserved = reservationTokens;
        if (limit > 0)
        {
            using var cmd = Command(db, tx, """
                SELECT COALESCE(SUM(consumed_input+consumed_output+
                    CASE WHEN state IN ('active','held') THEN
                        MAX(0,reserved_tokens-consumed_input-consumed_output) ELSE 0 END),0)
                FROM provider_turns WHERE project_id=$project AND provider_id=$provider;
                """, ("$project", projectId), ("$provider", provider.ProviderId));
            var allocated = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            reserved = Math.Min(reserved, limit - allocated);
            if (reserved < BudgetedAgentRuntime.MinimumTurnAllowance)
                throw new TokenBudgetExceededException("Provider token budget unavailable: " + provider.ProviderId);
        }
        var id = Guid.NewGuid();
        Execute(db, tx, """
            INSERT INTO provider_turns(id,project_id,job_id,execution_generation,provider_id,
                model,role,reserved_tokens,limited,state,created_at)
            VALUES($id,$project,$job,$generation,$provider,$model,$role,$reserved,$limited,'active',$at);
            """, ("$id", id), ("$project", projectId),
            ("$job", jobId.HasValue ? jobId.Value : null),
            ("$generation", fence?.Generation ?? 0), ("$provider", provider.ProviderId),
            ("$model", provider.ModelLabel), ("$role", provider.Role),
            ("$reserved", reserved), ("$limited", limit > 0), ("$at", DateTimeOffset.UtcNow));
        tx.Commit();
        return new ProviderTurnAllocation(id, reserved, limit > 0);
    }

    private static long DebitProviderTurn(SqliteConnection db, SqliteTransaction tx,
        Guid id, Guid? jobId, long inputDelta, long outputDelta)
    {
        if (inputDelta < 0 || outputDelta < 0)
            throw new ArgumentOutOfRangeException(nameof(inputDelta));
        using (var cmd = Command(db, tx, """
            UPDATE provider_turns SET consumed_input=consumed_input+$input,
                consumed_output=consumed_output+$output
            WHERE id=$id AND state='active'
                AND (($job IS NULL AND job_id IS NULL) OR job_id=$job);
            """, ("$id", id), ("$job", jobId), ("$input", inputDelta),
            ("$output", outputDelta)))
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Provider usage does not match active turn");
        using var select = Command(db, tx, """
            SELECT reserved_tokens,consumed_input,consumed_output,limited
            FROM provider_turns WHERE id=$id;
            """, ("$id", id));
        using var r = select.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Missing provider turn");
        var remaining = r.GetInt64(0) - checked(r.GetInt64(1) + r.GetInt64(2));
        return r.GetInt64(3) != 0 ? remaining : long.MaxValue;
    }

    public long RecordStandaloneProviderUsage(Guid turnId, long inputDelta, long outputDelta)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var balance = DebitProviderTurn(db, tx, turnId, null, inputDelta, outputDelta);
        tx.Commit();
        return balance;
    }

    public void FinishProviderTurn(Guid turnId, bool usageFinal, long? costMicros = null)
    {
        if (costMicros < 0) throw new ArgumentOutOfRangeException(nameof(costMicros));
        using var db = Open();
        using var tx = db.BeginTransaction();
        // A lost job lease cannot release provider reservations.
        using (var cmd = Command(db, tx, """
            UPDATE provider_turns SET
                state=CASE WHEN $complete=1 AND
                  (job_id IS NULL OR EXISTS (
                    SELECT 1 FROM jobs j WHERE j.id=provider_turns.job_id
                    AND j.execution_generation=provider_turns.execution_generation
                    AND j.lease_until>$now AND j.lease_owner!=''
                  )) THEN 'settled' ELSE 'held' END,
                reported_cost_micros=$cost,finished_at=$now
            WHERE id=$id AND state='active';
            """, ("$complete", usageFinal), ("$cost", costMicros),
            ("$now", DateTimeOffset.UtcNow), ("$id", turnId)))
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Provider turn already finalized or missing");
        tx.Commit();
    }

    public IReadOnlyList<ProviderCapacity> ProviderCapacities(Guid projectId)
    {
        using var db = Open();
        using var check = Command(db, null, "SELECT COUNT(*) FROM projects WHERE id=$id", ("$id", projectId));
        if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new ArgumentException("Unknown project");
        var result = ProviderRouting.Providers.ToDictionary(p => p, p => new ProviderCapacity
        {
            ProjectId = projectId, ProviderId = p
        }, StringComparer.Ordinal);
        using (var limits = Command(db, null, """
            SELECT provider_id,token_limit FROM provider_budget_limits WHERE project_id=$project
            """, ("$project", projectId)))
        using (var reader = limits.ExecuteReader())
            while (reader.Read())
                if (result.TryGetValue(reader.GetString(0), out var capacity) && reader.GetInt64(1) > 0)
                    capacity.TokenLimit = reader.GetInt64(1);

        using (var data = Command(db, null, """
            SELECT provider_id,model,role,consumed_input,consumed_output,reserved_tokens,
                state,reported_cost_micros
            FROM provider_turns WHERE project_id=$project ORDER BY created_at,id;
            """, ("$project", projectId)))
        using (var reader = data.ExecuteReader())
            while (reader.Read())
            {
                var provider = reader.GetString(0);
                if (!result.TryGetValue(provider, out var summary)) continue;
                var input = reader.GetInt64(3);
                var output = reader.GetInt64(4);
                var consumed = checked(input + output);
                summary.ConsumedTokens = checked(summary.ConsumedTokens + consumed);
                if (reader.GetString(6) is "active" or "held")
                    summary.OutstandingTokens = checked(summary.OutstandingTokens +
                        Math.Max(0, reader.GetInt64(5) - consumed));
                long? cost = reader.IsDBNull(7) ? null : reader.GetInt64(7);
                if (cost.HasValue)
                    summary.ReportedCostMicros = checked((summary.ReportedCostMicros ?? 0) + cost.Value);
                var model = reader.GetString(1);
                var role = reader.GetString(2);
                var row = summary.Breakdown.FirstOrDefault(x => x.Model == model && x.Role == role);
                if (row is null)
                {
                    row = new ProviderUsageBreakdown { Model = model, Role = role };
                    summary.Breakdown.Add(row);
                }
                row.InputTokens = checked(row.InputTokens + input);
                row.OutputTokens = checked(row.OutputTokens + output);
                if (cost.HasValue)
                    row.ReportedCostMicros = checked((row.ReportedCostMicros ?? 0) + cost.Value);
            }
        return result.Values.OrderBy(x => x.ProviderId).ToList();
    }
}

public sealed class ProjectMeteredRuntime(IAgentRuntime inner, StateStore store, Guid projectId,
    SelectedProvider provider, int reservationTokens) : IAgentRuntime
{
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        if (!readOnly)
            throw new InvalidOperationException("Project-level metered runtime is restricted to read-only operations");
        var turn = store.BeginProviderTurn(projectId, null, null, provider, reservationTokens);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        long recordedInput = 0, recordedOutput = 0;
        bool limitHit = false, finalized = false;
        void Record(AgentTokenUsage usage)
        {
            if (usage.InputTokens < recordedInput || usage.OutputTokens < recordedOutput) return;
            var inputDelta = usage.InputTokens - recordedInput;
            var outputDelta = usage.OutputTokens - recordedOutput;
            if (inputDelta == 0 && outputDelta == 0) return;
            var remaining = store.RecordStandaloneProviderUsage(turn.Id, inputDelta, outputDelta);
            recordedInput = usage.InputTokens;
            recordedOutput = usage.OutputTokens;
            onUsage?.Invoke(usage);
            if (remaining <= 0) { limitHit = true; linked.Cancel(); }
        }
        try
        {
            var result = await inner.RunAsync(directory, prompt, previousSession, linked.Token,
                onSession, true, Record);
            if (result.UsageReported)
                Record(new AgentTokenUsage(result.InputTokens, result.OutputTokens));
            if (limitHit)
                throw new TokenBudgetExceededException("Provider reservation exhausted during read-only operation");
            store.FinishProviderTurn(turn.Id, result.UsageReported && result.Success,
                result.ProviderReportedCostMicros);
            finalized = true;
            if (turn.Limited && !result.UsageReported)
                throw new TokenBudgetExceededException("Final provider usage unavailable; reservation remains held");
            return result;
        }
        catch (OperationCanceledException) when (limitHit && !cancellation.IsCancellationRequested)
        {
            throw new TokenBudgetExceededException("Provider reservation exhausted during read-only operation");
        }
        finally
        {
            if (!finalized)
            {
                try { store.FinishProviderTurn(turn.Id, false); }
                catch (InvalidOperationException) { /* Preserve the original failure. */ }
            }
        }
    }
}
