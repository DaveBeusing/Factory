namespace Factory;

/// <summary>
/// All ledger mutations are called inside StateStore.Change's SQLite write transaction.
/// Outstanding reservations count as liabilities even if worker usage is incomplete.
/// </summary>
public static class GoalBudgetLedger
{
    public const int DefaultReservationTokens = 8192;
    public const string Active = "active", Held = "held", Released = "released";

    private static long Spent(FactoryState state, Guid goalId)
        => checked(state.Jobs.Where(j => j.GoalId == goalId)
            .Aggregate(0L, (sum, job) => checked(sum + job.TokensIn + job.TokensOut)) +
            state.ProviderUsages.Where(u => u.GoalId == goalId && !u.JobId.HasValue)
                .Aggregate(0L, (sum, usage) => checked(sum + usage.InputTokens + usage.OutputTokens)));

    private static long Outstanding(FactoryState state, Guid goalId)
        => checked(state.BudgetReservations.Where(r => r.GoalId == goalId && r.State is Active or Held)
            .Aggregate(0L, (sum, r) => checked(sum + Math.Max(0, r.Reserved - r.Consumed))) +
            state.ProviderBudgetReservations.Where(r => r.State is Active or Held &&
                state.ProviderUsages.Any(u => u.Id == r.UsageId && !u.JobId.HasValue && u.GoalId == goalId))
                .Aggregate(0L, (sum, r) => checked(sum + Math.Max(0, r.Reserved - r.Consumed))));

    public static long Available(FactoryState state, Guid goalId, Guid? claimingJobId = null)
    {
        var goal = state.Goals.Single(g => g.Id == goalId);
        // A legacy active runner without a reservation has unbounded unknown liability.
        if (state.Jobs.Any(j => j.Id != claimingJobId && j.GoalId == goalId &&
            JobStates.Active.Contains(j.State) &&
            !string.IsNullOrWhiteSpace(j.LeaseOwner) &&
            !state.BudgetReservations.Any(r => r.JobId == j.Id && r.State == Active &&
                r.ExecutionGeneration == j.ExecutionGeneration)))
            return 0;
        return Math.Max(0, checked(goal.TokenBudget - Spent(state, goalId) - Outstanding(state, goalId)));
    }

    public static long Liability(FactoryState state, Guid goalId)
        => checked(Spent(state, goalId) + Outstanding(state, goalId));

    public static object Summary(FactoryState state, Guid goalId)
    {
        var goal = state.Goals.Single(g => g.Id == goalId);
        var consumed = Spent(state, goalId);
        var committed = Outstanding(state, goalId);
        var reservations = state.BudgetReservations.Where(r => r.GoalId == goalId).ToArray();
        var goalJobs = state.Jobs.Where(j => j.GoalId == goalId).ToArray();
        var usages = state.ProviderUsages.Where(u => u.GoalId == goalId).ToArray();
        var unsettledJobTurns = usages.Where(u => u.JobId.HasValue)
            .Join(state.ProviderBudgetReservations, u => u.Id, r => r.UsageId, (u, r) => (Usage: u, Hold: r))
            .Where(turn => !turn.Usage.FinalUsageReported || turn.Hold.State != Released)
            .ToLookup(turn => (turn.Usage.JobId, turn.Usage.ExecutionGeneration));
        var planningIds = usages.Where(u => !u.JobId.HasValue).Select(u => u.Id).ToHashSet();
        var planningReservations = state.ProviderBudgetReservations.Where(r => planningIds.Contains(r.UsageId)).ToArray();
        long PlanningOutstanding(string status) => planningReservations.Where(r => r.State == status)
            .Sum(r => Math.Max(0, r.Reserved - r.Consumed));
        return new
        {
            goalId, total = goal.TokenBudget, consumed, committed,
            activeOutstanding = reservations.Where(r => r.State == Active)
                .Sum(r => Math.Max(0, r.Reserved - r.Consumed)) + PlanningOutstanding(Active),
            heldOutstanding = reservations.Where(r => r.State == Held)
                .Sum(r => Math.Max(0, r.Reserved - r.Consumed)) + PlanningOutstanding(Held),
            providers = usages.GroupBy(u => new { u.ProviderId, u.Model, u.Role }).Select(group => new
            {
                group.Key.ProviderId, group.Key.Model, group.Key.Role,
                inputTokens = group.Sum(u => u.InputTokens), outputTokens = group.Sum(u => u.OutputTokens)
            }).ToArray(),
            planningHeld = PlanningOutstanding(Held),
            available = Available(state, goalId),
            requiresReconciliation = reservations.Any(r => r.State == Held),
            jobs = goalJobs.Select(j => new
            {
                j.Id, j.State, j.Stage, j.Error, j.ExecutionGeneration,
                worker = string.IsNullOrWhiteSpace(j.LeaseOwner) ? null : j.LeaseOwner,
                j.TokensIn, j.TokensOut, totalConsumed = checked(j.TokensIn + j.TokensOut),
                j.UsageComplete
            }).ToArray(),
            // Durable extension rows are independent of bounded operational events.
            extensions = state.BudgetExtensions.Where(e => e.GoalId == goalId)
                .Select(e => new { e.Id, e.JobId, e.ReservationId,
                    e.ExecutionGeneration, e.WorkerId, e.Granted,
                    e.AuthorizedTotal, e.At }).ToArray(),
            reservations = reservations.Select(r => new
                {
                    r.Id, r.JobId, r.ExecutionGeneration, r.State,
                    r.Reserved, r.Consumed,
                    outstanding = Math.Max(0, r.Reserved - r.Consumed),
                    pendingProviderTurns = unsettledJobTurns[((Guid?)r.JobId, r.ExecutionGeneration)].Count()
                }).ToArray()
        };
    }

    public static long Allocation(FactoryState state, FactoryJob job, int desired)
    {
        if (job.GoalId is not { } id) return 0;
        var goal = state.Goals.FirstOrDefault(g => g.Id == id);
        if (goal?.State != "active") return 0;
        // An unresolved previous attempt cannot reuse the same job's allowance.
        if (state.BudgetReservations.Any(r => r.JobId == job.Id && r.State == Held))
            return 0;
        if (desired < BudgetedAgentRuntime.MinimumTurnAllowance)
            throw new ArgumentOutOfRangeException(nameof(desired));
        var available = Available(state, id, job.Id);
        var amount = Math.Min(available, desired);
        return amount >= BudgetedAgentRuntime.MinimumTurnAllowance ? amount : 0;
    }

    public static GoalBudgetReservation? Reserve(FactoryState state, FactoryJob job, int desired)
    {
        if (job.GoalId is null) return null;
        if (job.State != JobStates.Preparing || job.ExecutionGeneration <= 0 ||
            string.IsNullOrWhiteSpace(job.LeaseToken))
            throw new InvalidOperationException("Goal reservation requires an active fenced claim");
        if (state.BudgetReservations.Any(r => r.JobId == job.Id && r.State == Active))
            throw new InvalidOperationException("Existing active reservation must be settled first");
        var allocated = Allocation(state, job, desired);
        if (allocated == 0) throw new TokenBudgetExceededException("Insufficient unreserved goal budget");
        var reservation = new GoalBudgetReservation
        {
            GoalId = job.GoalId.Value, JobId = job.Id, ExecutionGeneration = job.ExecutionGeneration,
            LeaseToken = job.LeaseToken, Reserved = allocated,
            BaseUsage = checked(job.TokensIn + job.TokensOut)
        };
        job.UsageComplete = false;
        state.BudgetReservations.Add(reservation);
        state.Events.Add(new FactoryEvent { JobId = job.Id, Type = "budget_reserved",
            Message = $"Generation {job.ExecutionGeneration}; reserved {allocated} tokens" });
        return reservation;
    }

    /// <summary>
    /// Extend an existing local execution reservation inside the caller's SQLite write
    /// transaction. The active lease, generation, goal and configured per-job cap
    /// remain authoritative; remote workers cannot mint their own allowance.
    /// </summary>
    public static long ExtendLocal(FactoryState state, FactoryJob job, int increment, int perJobLimit)
    {
        if (job.GoalId is null) return 0;
        if (increment < BudgetedAgentRuntime.MinimumTurnAllowance ||
            perJobLimit < BudgetedAgentRuntime.MinimumTurnAllowance)
            throw new ArgumentOutOfRangeException(nameof(increment));
        if (job.LeaseOwner.StartsWith("remote:", StringComparison.Ordinal))
            throw new InvalidOperationException("Remote reservations require controller authorization");
        JobRecovery.Require(job, new ExecutionFence(
            job.ExecutionGeneration, job.LeaseToken, job.LeaseOwner));
        var reservation = Current(state, job);
        if (state.Goals.Single(g => g.Id == job.GoalId.Value).State != "active")
            throw new TokenBudgetExceededException("Goal is not active");
        var room = Math.Max(0L, perJobLimit - reservation.Reserved);
        var available = Available(state, job.GoalId.Value);
        var granted = Math.Min(Math.Min(room, available), increment);
        if (granted < BudgetedAgentRuntime.MinimumTurnAllowance) return 0;
        reservation.Reserved = checked(reservation.Reserved + granted);
        reservation.UpdatedAt = DateTimeOffset.UtcNow;
        state.BudgetExtensions.Add(new GoalBudgetExtension
        {
            GoalId = reservation.GoalId, JobId = job.Id, ReservationId = reservation.Id,
            ExecutionGeneration = job.ExecutionGeneration, WorkerId = job.LeaseOwner,
            Granted = granted, AuthorizedTotal = reservation.Reserved
        });
        state.Events.Add(new FactoryEvent
        {
            JobId = job.Id, Type = "budget_extended",
            Message = $"Generation {job.ExecutionGeneration}; +{granted} tokens; reservation {reservation.Id}; total {reservation.Reserved}"
        });
        return granted;
    }

    /// <summary>
    /// Authorize an explicit remote-worker extension under the controller's
    /// atomic state mutation. The request ID is the durable replay key.
    /// An expired or superseded worker cannot obtain or replay a grant.
    /// </summary>
    public static GoalBudgetExtension ExtendRemote(FactoryState state, FactoryJob job,
        ExecutionFence fence, Guid requestId, int requestedTokens, int maxIncrement,
        int perJobLimit)
    {
        if (job.GoalId is null || requestId == Guid.Empty ||
            job.Mode != JobExecutionModes.Remote ||
            !job.LeaseOwner.StartsWith("remote:", StringComparison.Ordinal))
            throw new InvalidOperationException("Active remote goal reservation required");
        JobRecovery.Require(job, fence);
        if (requestedTokens < BudgetedAgentRuntime.MinimumTurnAllowance ||
            maxIncrement < BudgetedAgentRuntime.MinimumTurnAllowance ||
            perJobLimit < BudgetedAgentRuntime.MinimumTurnAllowance)
            throw new ArgumentOutOfRangeException(nameof(requestedTokens));
        var reservation = Current(state, job);
        var prior = state.BudgetExtensions.FirstOrDefault(e => e.Id == requestId);
        if (prior is not null)
        {
            if (prior.JobId != job.Id || prior.ReservationId != reservation.Id ||
                prior.ExecutionGeneration != job.ExecutionGeneration ||
                prior.WorkerId != fence.Owner)
                throw new InvalidOperationException("Extension request ID belongs to another execution");
            return prior;
        }
        if (state.Goals.Single(g => g.Id == job.GoalId.Value).State != "active")
            throw new TokenBudgetExceededException("Goal is not active");
        var room = Math.Max(0L, perJobLimit - reservation.Reserved);
        var available = Available(state, job.GoalId.Value);
        var amount = Math.Min(Math.Min(room, available), Math.Min(requestedTokens, maxIncrement));
        var granted = amount >= BudgetedAgentRuntime.MinimumTurnAllowance ? amount : 0;
        reservation.Reserved = checked(reservation.Reserved + granted);
        reservation.UpdatedAt = DateTimeOffset.UtcNow;
        var extension = new GoalBudgetExtension
        {
            Id = requestId, GoalId = reservation.GoalId, JobId = job.Id,
            ReservationId = reservation.Id, ExecutionGeneration = job.ExecutionGeneration,
            WorkerId = fence.Owner, Granted = granted, AuthorizedTotal = reservation.Reserved
        };
        state.BudgetExtensions.Add(extension);
        if (granted > 0)
            state.Events.Add(new FactoryEvent { JobId = job.Id, Type = "budget_extended",
                Message = $"Remote generation {job.ExecutionGeneration}; +{granted} tokens; reservation {reservation.Id}; total {reservation.Reserved}" });
        return extension;
    }

    public static GoalBudgetReservation Current(FactoryState state, FactoryJob job)
    {
        if (job.GoalId is null) throw new InvalidOperationException("Job has no goal budget");
        return state.BudgetReservations.SingleOrDefault(r =>
            r.JobId == job.Id && r.GoalId == job.GoalId &&
            r.ExecutionGeneration == job.ExecutionGeneration &&
            r.LeaseToken == job.LeaseToken && r.State == Active)
            ?? throw new TokenBudgetExceededException("No active fenced goal reservation");
    }

    public static long Remaining(FactoryState state, FactoryJob job)
        => job.GoalId is null ? long.MaxValue :
            Math.Max(0, Current(state, job).Reserved - Current(state, job).Consumed);

    public static long BeginTurn(FactoryState state, FactoryJob job)
    {
        if (job.GoalId is null) return long.MaxValue;
        var remaining = Remaining(state, job);
        if (remaining < BudgetedAgentRuntime.MinimumTurnAllowance)
            throw new TokenBudgetExceededException(
                $"Insufficient job reservation for another turn ({remaining} tokens available)");
        job.UsageComplete = false;
        return remaining;
    }

    // 'cumulative' values include prior attempts; debit only the new delta.
    // Never hide an overrun: record actual observed usage before requesting cancellation.
    public static long Record(FactoryState state, FactoryJob job, long cumulativeIn,
        long cumulativeOut, bool? usageComplete = null)
    {
        if (cumulativeIn < 0 || cumulativeOut < 0)
            throw new InvalidOperationException("Negative token usage rejected");
        if (job.GoalId is not null) Current(state, job);
        job.TokensIn = Math.Max(job.TokensIn, cumulativeIn);
        job.TokensOut = Math.Max(job.TokensOut, cumulativeOut);
        if (usageComplete.HasValue) job.UsageComplete = usageComplete.Value;
        if (job.GoalId is null) return long.MaxValue;
        var reservation = Current(state, job);
        var consumed = checked(job.TokensIn + job.TokensOut - reservation.BaseUsage);
        if (consumed < reservation.Consumed)
            throw new InvalidOperationException("Goal reservation usage cannot regress");
        reservation.Consumed = consumed;
        reservation.UpdatedAt = DateTimeOffset.UtcNow;
        return reservation.Reserved - reservation.Consumed;
    }

    public static void Hold(FactoryState state, FactoryJob job, string reason)
    {
        if (job.GoalId is null) return;
        var reservation = state.BudgetReservations.FirstOrDefault(r =>
            r.JobId == job.Id && r.ExecutionGeneration == job.ExecutionGeneration &&
            r.State == Active);
        if (reservation is null) return;
        reservation.State = Held;
        reservation.UpdatedAt = DateTimeOffset.UtcNow;
        state.Events.Add(new FactoryEvent { JobId = job.Id, Type = "budget_held",
            Message = $"Unsettled allowance {Math.Max(0, reservation.Reserved - reservation.Consumed)}; {reason}" });
    }

    // Called inside the same transaction that clears an execution lease.
    public static void Settle(FactoryState state, FactoryJob job)
    {
        if (job.GoalId is null) return;
        var reservation = Current(state, job);
        if (job.State is JobStates.CompletedLocal or JobStates.PrReady && job.UsageComplete &&
            reservation.Consumed <= reservation.Reserved)
        {
            reservation.State = Released;
            reservation.UpdatedAt = DateTimeOffset.UtcNow;
            state.Events.Add(new FactoryEvent { JobId = job.Id, Type = "budget_released",
                Message = $"Released {reservation.Reserved - reservation.Consumed} unused tokens" });
        }
        else Hold(state, job, job.UsageComplete ? "job did not complete" : "final usage unavailable");
    }

    internal static GoalBudgetReservation? LinkedReservation(FactoryState state, ProviderUsageEntry usage) =>
        usage.JobId is null || usage.GoalId is null ? null : state.BudgetReservations.SingleOrDefault(r =>
            r.JobId == usage.JobId && r.GoalId == usage.GoalId && r.ExecutionGeneration == usage.ExecutionGeneration);

    internal static GoalBudgetReservation? ApplyProviderReconciliation(FactoryState state, FactoryJob job,
        ProviderUsageEntry usage, long inputDelta, long outputDelta, bool confirmOverrun)
    {
        if (usage.GoalId is null) return null;
        var reservation = LinkedReservation(state, usage)
            ?? throw new InvalidOperationException("Matching goal budget reservation is missing; reconcile the evidence manually");
        // Older versions permitted settling goal and provider liabilities independently.
        // A verified zero delta can settle the remaining provider hold without changing the goal.
        if (inputDelta == 0 && outputDelta == 0 && reservation.State == Released) return reservation;
        if (reservation.State != Held || state.BudgetReservations.Any(r =>
            r.JobId == job.Id && r.ExecutionGeneration > reservation.ExecutionGeneration))
            throw new InvalidOperationException("Late usage can only adjust the latest held goal reservation");
        if (job.GoalId != reservation.GoalId ||
            checked(job.TokensIn + job.TokensOut - reservation.BaseUsage) != reservation.Consumed)
            throw new InvalidOperationException("Goal and job accounting disagree; inspect evidence before reconciliation");

        var consumed = checked(reservation.Consumed + inputDelta + outputDelta);
        RequireVerifiedOverrun(reservation.Reserved, consumed, confirmOverrun);
        job.TokensIn = checked(job.TokensIn + inputDelta);
        job.TokensOut = checked(job.TokensOut + outputDelta);
        reservation.Consumed = consumed;
        reservation.UpdatedAt = DateTimeOffset.UtcNow;
        return reservation;
    }

    internal static void RequireVerifiedOverrun(long reserved, long consumed, bool confirmed)
    {
        if (consumed > reserved && !confirmed)
            throw new InvalidOperationException("Claimed usage exceeds the reserved budget; explicitly confirm the verified overrun");
    }

    /// <summary>
    /// Explicit audited settlement. Uncertain worker usage is never silently forgiven.
    /// </summary>
    public static GoalBudgetReservation Reconcile(StateStore store, Guid goalId, Guid reservationId,
        long finalTokensIn, long finalTokensOut, bool reviewed, string reason, bool confirmOverrun = false)
    {
        if (!reviewed || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            throw new InvalidOperationException("Independent usage reconciliation requires review and justification");
        return store.Change(state =>
        {
            var reservation = state.BudgetReservations.Single(r => r.Id == reservationId);
            if (reservation.GoalId != goalId)
                throw new InvalidOperationException("Reservation belongs to a different goal");
            if (reservation.State != Held)
                throw new InvalidOperationException("Only held reservations can be reconciled");
            var job = state.Jobs.Single(j => j.Id == reservation.JobId);
            if (!string.IsNullOrEmpty(job.LeaseOwner) || JobStates.Active.Contains(job.State))
                throw new InvalidOperationException("Cannot reconcile a running job");
            if (finalTokensIn < job.TokensIn || finalTokensOut < job.TokensOut)
                throw new InvalidOperationException("Final token usage cannot be below observed usage");
            var linkedTurns = state.ProviderUsages.Where(u => u.JobId == job.Id && u.GoalId == goalId &&
                u.ExecutionGeneration == reservation.ExecutionGeneration)
                .Join(state.ProviderBudgetReservations, u => u.Id, r => r.UsageId, (u, r) => (Usage: u, Hold: r))
                .ToArray();
            if (linkedTurns.Any(turn => !turn.Usage.FinalUsageReported || turn.Hold.State != Released))
                throw new InvalidOperationException("Reconcile outstanding provider turns before settling their goal reservation");
            if (linkedTurns.Length > 0 && (finalTokensIn != job.TokensIn || finalTokensOut != job.TokensOut))
                throw new InvalidOperationException("Record additional usage on its provider turn before settling the linked goal reservation");
            if (state.BudgetReservations.Any(r => r.JobId == job.Id && r.ExecutionGeneration > reservation.ExecutionGeneration))
                throw new InvalidOperationException("A newer goal reservation exists for this job");
            var usage = checked(finalTokensIn + finalTokensOut);
            var consumed = checked(usage - reservation.BaseUsage);
            if (consumed < reservation.Consumed)
                throw new InvalidOperationException("Goal reservation usage cannot regress");
            RequireVerifiedOverrun(reservation.Reserved, consumed, confirmOverrun);
            job.TokensIn = finalTokensIn;
            job.TokensOut = finalTokensOut;
            job.UsageComplete = true;
            reservation.Consumed = consumed;
            reservation.State = Released;
            reservation.UpdatedAt = DateTimeOffset.UtcNow;
            state.Events.Add(new FactoryEvent { JobId = job.Id, Type = "budget_reconciled",
                Message = $"Reservation {reservation.Id}; consumed {consumed}; verified overrun {Math.Max(0, consumed - reservation.Reserved)}; {reason.Trim()[..Math.Min(1000, reason.Trim().Length)]}" });
            return reservation;
        });
    }
}
