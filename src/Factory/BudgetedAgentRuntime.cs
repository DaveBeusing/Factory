namespace Factory;

public sealed class TokenBudgetExceededException(string message) : InvalidOperationException(message) { }

/// <summary>
/// Every turn consumes an independently reserved share of the goal allowance.
/// Transactional debits and execution fencing prevent concurrent jobs from
/// spending the same available budget. Turns of one job remain serialized.
/// </summary>
public sealed record GoalBudgetExtensionPolicy(int IncrementTokens, int ThresholdTokens, int MaxReservationTokens);

public sealed class BudgetedAgentRuntime(IAgentRuntime inner, StateStore store, Guid jobId,
    ExecutionFence? fence = null, GoalBudgetExtensionPolicy? extensionPolicy = null,
    Func<CancellationToken, Task>? controllerExtension = null, int controllerThresholdTokens = 1024,
    SelectedProvider? provider = null, int providerReservationTokens = GoalBudgetLedger.DefaultReservationTokens) : IAgentRuntime
{
    public const long MinimumTurnAllowance = 256;
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        await _turnLock.WaitAsync(cancellation);
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
                return GoalBudgetLedger.BeginTurn(state, job);
            });

            ProviderTurnAllocation? providerTurn = provider is null ? null :
                store.BeginProviderTurn(store.Job(jobId).ProjectId, jobId, fence,
                    provider, providerReservationTokens);
            bool providerTurnFinalized = false;
            using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            long recordedIn = 0, recordedOut = 0;
            bool budgetReached = false;
            bool sawUsage = false;

            void Record(AgentTokenUsage usage)
            {
                if (usage.InputTokens < recordedIn || usage.OutputTokens < recordedOut)
                    return; // An old or repeated cumulative snapshot is not a debit.
                var inputDelta = usage.InputTokens - recordedIn;
                var outputDelta = usage.OutputTokens - recordedOut;
                if (inputDelta == 0 && outputDelta == 0 && sawUsage) return;

                long currentRemaining;
                if (extensionPolicy is not null && goalFunded)
                {
                    long DebitGoal(FactoryState state)
                    {
                        var job = state.Jobs.Single(j => j.Id == jobId);
                        if (fence is { } currentFence) JobRecovery.Require(job, currentFence);
                        var balance = GoalBudgetLedger.Record(state, job,
                            checked(job.TokensIn + inputDelta),
                            checked(job.TokensOut + outputDelta));
                        if (balance >= 0 && balance <= extensionPolicy.ThresholdTokens)
                        {
                            GoalBudgetLedger.ExtendLocal(state, job, extensionPolicy.IncrementTokens,
                                extensionPolicy.MaxReservationTokens);
                            balance = GoalBudgetLedger.Remaining(state, job);
                        }
                        return balance;
                    }
                    if (providerTurn is { } activeTurn)
                    {
                        var balances = store.ChangeWithProviderDebit(DebitGoal,
                            activeTurn.Id, jobId, inputDelta, outputDelta);
                        currentRemaining = Math.Min(balances.GoalRemaining, balances.ProviderRemaining);
                    }
                    else currentRemaining = store.Change(DebitGoal);
                }
                else if (fence is { } activeFence)
                    currentRemaining = store.RecordFencedUsage(jobId, activeFence,
                        inputDelta, outputDelta, providerTurnId: providerTurn?.Id);
                else
                    currentRemaining = store.Change(state =>
                    {
                        var job = state.Jobs.Single(j => j.Id == jobId);
                        return GoalBudgetLedger.Record(state, job,
                            checked(job.TokensIn + inputDelta),
                            checked(job.TokensOut + outputDelta));
                    });

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
                    Record(new AgentTokenUsage(result.InputTokens, result.OutputTokens));

                // A partial usage notification cannot prove the final total.
                if (!result.UsageReported && (remaining != long.MaxValue || providerTurn?.Limited == true))
                    throw new TokenBudgetExceededException("Agent did not report final token usage; budget remains held");

                if (result.UsageReported && remaining != long.MaxValue && !budgetReached)
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
                    throw new TokenBudgetExceededException("Job reservation exhausted during execution");
                if (fence is { } finalFence) JobRecovery.Require(store.Job(jobId), finalFence);
                if (providerTurn is { } completedTurn)
                {
                    store.FinishProviderTurn(completedTurn.Id, result.UsageReported && result.Success,
                        result.ProviderReportedCostMicros);
                    providerTurnFinalized = true;
                }
                return result;
            }
            catch (OperationCanceledException) when (budgetReached && !cancellation.IsCancellationRequested)
            {
                if (providerTurn is { } unfinishedTurn && !providerTurnFinalized)
                {
                    try { store.FinishProviderTurn(unfinishedTurn.Id, false); }
                    catch (InvalidOperationException) { /* Preserve budget failure. */ }
                }
                throw new TokenBudgetExceededException("Job or provider reservation exhausted during execution");
            }
            catch
            {
                if (providerTurn is { } unfinishedTurn && !providerTurnFinalized)
                {
                    try { store.FinishProviderTurn(unfinishedTurn.Id, false); }
                    catch (InvalidOperationException) { /* Preserve the original error; leave usage held. */ }
                }
                throw;
            }
        }
        finally { _turnLock.Release(); }
    }
}
