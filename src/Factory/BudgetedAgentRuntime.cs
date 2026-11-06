namespace Factory;

public sealed class TokenBudgetExceededException(string message) : InvalidOperationException(message) { }

/// <summary>
/// Every turn consumes an independently reserved share of the goal allowance.
/// Transactional debits and execution fencing prevent concurrent jobs from
/// spending the same available budget. Turns of one job remain serialized.
/// </summary>
public sealed record GoalBudgetExtensionPolicy(int IncrementTokens, int ThresholdTokens, int MaxReservationTokens);

public sealed record JobProviderMeter(ProviderProfile Profile, string Role, string Model, int ReservationTokens);

public sealed class BudgetedAgentRuntime(IAgentRuntime inner, StateStore store, Guid jobId,
    ExecutionFence? fence = null, GoalBudgetExtensionPolicy? extensionPolicy = null,
    Func<CancellationToken, Task>? controllerExtension = null, int controllerThresholdTokens = 1024,
    JobProviderMeter? provider = null) : IAgentRuntime
{
    public const long MinimumTurnAllowance = 256;
    // A single job can invoke different providers for building and independent reviews.
    // Its goal ledger must serialize turns across all of those adapters.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> TurnLocks = new();
    private SemaphoreSlim TurnLock => TurnLocks.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));

    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        await TurnLock.WaitAsync(cancellation);
        try
        {
            // Remote workers may synchronize a larger local mirror only after receiving
            // an authenticated controller grant. A missing or failed grant does not
            // authorize the worker to lift its own reservation.
            if (controllerExtension is not null)
            {
                var snapshot = store.Read();
                var current = snapshot.Jobs.Single(j => j.Id == jobId);
                if (current.GoalId.HasValue &&
                    GoalBudgetLedger.Remaining(snapshot, current) <= controllerThresholdTokens)
                    await controllerExtension(cancellation);
            }
            // Regular fenced usage retains the focused SQL path. A local extension
            // must debit consumption and extend its reservation atomically.
            bool goalFunded = false;
            Guid? providerUsageId = null;
            var remaining = store.Change(state =>
            {
                var job = state.Jobs.Single(j => j.Id == jobId);
                goalFunded = job.GoalId.HasValue;
                if (job.GoalId.HasValue && fence is null)
                    throw new InvalidOperationException(
                        "A goal-funded agent invocation requires a fenced worker claim");
                if (fence is { } currentFence) JobRecovery.Require(job, currentFence);
                if (job.GoalId is { } goalId &&
                    state.Goals.Single(g => g.Id == goalId).State != "active")
                    throw new TokenBudgetExceededException("Goal is not active");
                if (extensionPolicy is not null && job.GoalId.HasValue &&
                    GoalBudgetLedger.Remaining(state, job) <= extensionPolicy.ThresholdTokens)
                    GoalBudgetLedger.ExtendLocal(state, job, extensionPolicy.IncrementTokens,
                        extensionPolicy.MaxReservationTokens);
                var available = GoalBudgetLedger.BeginTurn(state, job);
                if (provider is not null)
                    providerUsageId = ProviderBudgetLedger.Begin(state, job, provider.Profile,
                        provider.Role, provider.Model, provider.ReservationTokens).Id;
                return available;
            });

            using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            long recordedIn = 0, recordedOut = 0;
            bool budgetReached = false;
            bool sawUsage = false;
            bool finalUsage = false;

            void Record(AgentTokenUsage usage)
            {
                if (usage.InputTokens < recordedIn || usage.OutputTokens < recordedOut)
                    return; // An old or repeated cumulative snapshot is not a debit.
                var inputDelta = usage.InputTokens - recordedIn;
                var outputDelta = usage.OutputTokens - recordedOut;
                if (inputDelta == 0 && outputDelta == 0 && sawUsage) return;

                var currentRemaining = providerUsageId.HasValue || extensionPolicy is not null && goalFunded || fence is null
                    ? store.Change(state =>
                    {
                        var job = state.Jobs.Single(j => j.Id == jobId);
                        if (fence is { } currentFence) JobRecovery.Require(job, currentFence);
                        var balance = GoalBudgetLedger.Record(state, job,
                            checked(job.TokensIn + inputDelta),
                            checked(job.TokensOut + outputDelta));
                        if (extensionPolicy is not null && goalFunded &&
                            balance >= 0 && balance <= extensionPolicy.ThresholdTokens)
                        {
                            GoalBudgetLedger.ExtendLocal(state, job, extensionPolicy.IncrementTokens,
                                extensionPolicy.MaxReservationTokens);
                            balance = GoalBudgetLedger.Remaining(state, job);
                        }
                        // Both ledgers must commit or roll back together.
                        if (providerUsageId is { } id)
                            balance = Math.Min(balance, ProviderBudgetLedger.Record(state, id, usage));
                        return balance;
                    })
                    : store.RecordFencedUsage(jobId, fence!.Value, inputDelta, outputDelta);

                recordedIn = usage.InputTokens;
                recordedOut = usage.OutputTokens;
                sawUsage = true;
                onUsage?.Invoke(usage);

                // The authorized reservation may have been extended during this usage debit.
                if (currentRemaining <= 0)
                {
                    budgetReached = true;
                    scope.Cancel();
                }
            }

            try
            {
                var result = await inner.RunAsync(directory, prompt, previousSession, scope.Token,
                    onSession, readOnly, Record);
                if (result.UsageReported)
                {
                    Record(new AgentTokenUsage(result.InputTokens, result.OutputTokens));
                    finalUsage = result.InputTokens == recordedIn && result.OutputTokens == recordedOut;
                }

                // A partial usage notification cannot prove the final total.
                if (!finalUsage && remaining != long.MaxValue)
                    throw new TokenBudgetExceededException("Agent did not report final token usage; budget remains held");

                if (finalUsage && remaining != long.MaxValue && !budgetReached)
                {
                    if (fence is { } finalUsageFence)
                        store.RecordFencedUsage(jobId, finalUsageFence, 0, 0, usageComplete: true);
                    else
                        store.Change(state =>
                        {
                            var job = state.Jobs.Single(j => j.Id == jobId);
                            GoalBudgetLedger.Current(state, job);
                            job.UsageComplete = true;
                        });
                }

                if (budgetReached)
                    throw new TokenBudgetExceededException("Job or provider reservation exhausted during execution");
                if (fence is { } finalFence) JobRecovery.Require(store.Job(jobId), finalFence);
                return result;
            }
            catch (OperationCanceledException) when (budgetReached && !cancellation.IsCancellationRequested)
            {
                throw new TokenBudgetExceededException("Job or provider reservation exhausted during execution");
            }
            finally
            {
                if (providerUsageId is { } id)
                    store.Change(state => ProviderBudgetLedger.Finish(state, id, finalUsage));
            }
        }
        finally { TurnLock.Release(); }
    }
}
