namespace Factory;

public sealed class AgentRunResult
{
    public bool Success { get; set; }
    public string FinalText { get; set; } = "";
    public bool FinalTextIsFallback { get; set; }
    public string Error { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ObservedModel { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public bool UsageReported { get; set; }
    public long? ProviderReportedCostMicros { get; set; }
}

public readonly record struct AgentTokenUsage(long InputTokens, long OutputTokens)
{
    public long Total => checked(InputTokens + OutputTokens);
}

public interface IAgentRuntime
{
    Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null);
}

public static class AgentRuntimeFactory
{
    public static IAgentRuntime Create(FactorySettings settings, string risk = "medium")
    {
        var model = settings.ModelRoutes.GetValueOrDefault(risk);
        if (string.IsNullOrWhiteSpace(model)) model = settings.Model;
        IAgentRuntime inner = settings.Runtime == "app-server"
            ? new AppServerAgentRuntime(model) : new ExecAgentRuntime(model);
        return new SlottedAgentRuntime(inner, Math.Max(1, settings.MaxAgentSessions));
    }
}

public sealed class SlottedAgentRuntime(IAgentRuntime inner, int maxSessions) : IAgentRuntime
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> Slots = new();
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        var semaphore = Slots.GetOrAdd(maxSessions, n => new SemaphoreSlim(n, n));
        await semaphore.WaitAsync(cancellation);
        try { return await inner.RunAsync(directory, prompt, previousSession, cancellation, onSession, readOnly, onUsage); }
        finally { semaphore.Release(); }
    }
}
