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
        => state.Jobs.Where(j => j.GoalId == goalId)
            .Aggregate(0L, (sum, job) => checked(sum + job.TokensIn + job.TokensOut));

    private static long Outstanding(FactoryState state, Guid goalId)
        => state.BudgetReservations.Where(r => r.GoalId == goalId && r.State is Active or Held)
            .Aggregate(0L, (sum, r) => checked(sum + Math.Max(0, r.Reserved - r.Consumed)));

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

    public static long Allocation(FactoryState state, FactoryJob job, int desired)
    {
        if (job.GoalId is not { } id) return 0;
        var goal = state.Goals.FirstOrDefault(g => g.Id == id);
        if (goal?.State != "active") return 0;
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

    /// <summary>
    /// Explicit audited settlement. Uncertain worker usage is never silently forgiven.
    /// </summary>
    public static GoalBudgetReservation Reconcile(StateStore store, Guid reservationId,
        long finalTokensIn, long finalTokensOut, bool reviewed, string reason)
    {
        if (!reviewed || reason.Trim().Length < 10)
            throw new InvalidOperationException("Independent usage reconciliation requires review and justification");
        return store.Change(state =>
        {
            var reservation = state.BudgetReservations.Single(r => r.Id == reservationId);
            if (reservation.State != Held)
                throw new InvalidOperationException("Only held reservations can be reconciled");
            var job = state.Jobs.Single(j => j.Id == reservation.JobId);
            if (!string.IsNullOrEmpty(job.LeaseOwner) || JobStates.Active.Contains(job.State))
                throw new InvalidOperationException("Cannot reconcile a running job");
            if (finalTokensIn < job.TokensIn || finalTokensOut < job.TokensOut)
                throw new InvalidOperationException("Final token usage cannot be below observed usage");
            var usage = checked(finalTokensIn + finalTokensOut);
            var consumed = checked(usage - reservation.BaseUsage);
            if (consumed < 0 || consumed > reservation.Reserved)
                throw new InvalidOperationException("Claimed usage exceeds the reserved budget; manual escalation required");
            job.TokensIn = finalTokensIn;
            job.TokensOut = finalTokensOut;
            job.UsageComplete = true;
            reservation.Consumed = consumed;
            reservation.State = Released;
            reservation.UpdatedAt = DateTimeOffset.UtcNow;
            state.Events.Add(new FactoryEvent { JobId = job.Id, Type = "budget_reconciled",
                Message = $"Reservation {reservation.Id}; consumed {consumed}; {reason.Trim()[..Math.Min(1000, reason.Trim().Length)]}" });
            return reservation;
        });
    }
}
