namespace Factory;

public sealed class TokenBudgetExceededException(string message) : InvalidOperationException(message) { }

/// <summary>
/// Accounts for all builder and reviewer turns, including streamed partial usage,
/// before allowing the next turn. Serializes turns within a job to prevent its
/// reviewers racing the same remaining goal budget.
/// </summary>
public sealed class BudgetedAgentRuntime(IAgentRuntime inner, StateStore store, Guid jobId) : IAgentRuntime
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
            var state = store.Read();
            var job = state.Jobs.Single(j => j.Id == jobId);
            long? remaining = null;
            if (job.GoalId is { } goalId)
            {
                var goal = state.Goals.Single(g => g.Id == goalId);
                if (goal.State != "active")
                    throw new TokenBudgetExceededException("Goal is not active");
                var spent = state.Jobs.Where(j => j.GoalId == goalId)
                    .Aggregate(0L, (sum, j) => checked(sum + j.TokensIn + j.TokensOut));
                remaining = goal.TokenBudget - spent;
                if (remaining < MinimumTurnAllowance)
                    throw new TokenBudgetExceededException(
                        $"Insufficient goal budget for another turn ({Math.Max(0, remaining.Value)} remaining, " +
                        $"{MinimumTurnAllowance} minimum).");
            }

            using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            long recordedIn = 0, recordedOut = 0;
            bool budgetReached = false;
            bool sawUsage = false;
            void Record(AgentTokenUsage usage)
            {
                if (usage.InputTokens < recordedIn || usage.OutputTokens < recordedOut)
                    return; // Ignore old or duplicated cumulative snapshots.
                var inputDelta = usage.InputTokens - recordedIn;
                var outputDelta = usage.OutputTokens - recordedOut;
                if (inputDelta == 0 && outputDelta == 0 && sawUsage) return;
                recordedIn = usage.InputTokens;
                recordedOut = usage.OutputTokens;
                sawUsage = true;
                if (inputDelta != 0 || outputDelta != 0)
                    store.UpdateJob(jobId, j =>
                    {
                        j.TokensIn = checked(j.TokensIn + inputDelta);
                        j.TokensOut = checked(j.TokensOut + outputDelta);
                    });
                onUsage?.Invoke(usage);
                if (remaining.HasValue && usage.Total >= remaining.Value)
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
                if (budgetReached)
                    throw new TokenBudgetExceededException("Goal token budget reached during execution");
                // Unknown usage must not silently permit unlimited further turns.
                if (remaining.HasValue && !sawUsage)
                    throw new TokenBudgetExceededException("Agent did not report token usage; budget cannot be verified");
                return result;
            }
            catch (OperationCanceledException) when (budgetReached && !cancellation.IsCancellationRequested)
            {
                throw new TokenBudgetExceededException("Goal token budget reached during execution");
            }
        }
        finally { _turnLock.Release(); }
    }
}
