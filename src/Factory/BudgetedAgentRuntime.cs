namespace Factory;

public sealed class TokenBudgetExceededException(string message) : InvalidOperationException(message) { }

/// <summary>
/// Every turn consumes an independently reserved share of the goal allowance.
/// Transactional debits and execution fencing prevent concurrent jobs from
/// spending the same available budget. Turns of one job remain serialized.
/// </summary>
public sealed class BudgetedAgentRuntime(IAgentRuntime inner, StateStore store, Guid jobId,
    ExecutionFence? fence = null) : IAgentRuntime
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
            var remaining = store.Change(state =>
            {
                var job = state.Jobs.Single(j => j.Id == jobId);
                if (job.GoalId.HasValue && fence is null)
                    throw new InvalidOperationException(
                        "A goal-funded agent invocation requires a fenced worker claim");
                if (fence is { } currentFence) JobRecovery.Require(job, currentFence);
                if (job.GoalId is { } goalId &&
                    state.Goals.Single(g => g.Id == goalId).State != "active")
                    throw new TokenBudgetExceededException("Goal is not active");
                return GoalBudgetLedger.BeginTurn(state, job);
            });

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

                var currentRemaining = store.Change(state =>
                {
                    var job = state.Jobs.Single(j => j.Id == jobId);
                    if (fence is { } currentFence) JobRecovery.Require(job, currentFence);
                    return GoalBudgetLedger.Record(state, job,
                        checked(job.TokensIn + inputDelta),
                        checked(job.TokensOut + outputDelta));
                });

                recordedIn = usage.InputTokens;
                recordedOut = usage.OutputTokens;
                sawUsage = true;
                onUsage?.Invoke(usage);

                if (remaining != long.MaxValue &&
                    (usage.Total >= remaining || currentRemaining <= 0))
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
                if (!result.UsageReported && remaining != long.MaxValue)
                    throw new TokenBudgetExceededException("Agent did not report final token usage; budget remains held");

                if (result.UsageReported && remaining != long.MaxValue && !budgetReached)
                    store.Change(state =>
                    {
                        var job = state.Jobs.Single(j => j.Id == jobId);
                        if (fence is { } currentFence) JobRecovery.Require(job, currentFence);
                        GoalBudgetLedger.Current(state, job);
                        job.UsageComplete = true;
                    });

                if (budgetReached)
                    throw new TokenBudgetExceededException("Job reservation exhausted during execution");
                if (fence is { } finalFence) JobRecovery.Require(store.Job(jobId), finalFence);
                return result;
            }
            catch (OperationCanceledException) when (budgetReached && !cancellation.IsCancellationRequested)
            {
                throw new TokenBudgetExceededException("Job reservation exhausted during execution");
            }
        }
        finally { _turnLock.Release(); }
    }
}
