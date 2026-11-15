namespace Factory;

public sealed class AgentRunResult
{
    public bool Success { get; set; }
    public string FinalText { get; set; } = "";
    public bool FinalTextIsFallback { get; set; }
    public string Error { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string RolePolicyRevision { get; set; } = "";
    public string RequestedModel { get; set; } = "";
    public string ObservedModel { get; set; } = "";
    public IReadOnlyList<string> ObservedModels { get; set; } = [];
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

internal interface IAgentRuntimeAdmission
{
    Task VerifyAdmissionAsync(string previousSession, bool readOnly, CancellationToken cancellation);

    internal static Task VerifyAsync(IAgentRuntime runtime, string previousSession, bool readOnly,
        CancellationToken cancellation) =>
        runtime is IAgentRuntimeAdmission admission
            ? admission.VerifyAdmissionAsync(previousSession, readOnly, cancellation)
            : Task.CompletedTask;
}

public static class AgentRuntimeFactory
{
    public static IAgentRuntime Create(FactorySettings settings, string risk = "medium")
    {
        var model = settings.ModelRoutes.GetValueOrDefault(risk);
        if (string.IsNullOrWhiteSpace(model)) model = settings.Model;
        IAgentRuntime inner = settings.Runtime == "app-server"
            ? new AppServerAgentRuntime(model) : new ExecAgentRuntime(model);
        return new SlottedAgentRuntime(inner, settings);
    }
}

public sealed class SlottedAgentRuntime : IAgentRuntime, IAgentRuntimeAdmission
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, AgentSessionLimiter> FixedLimits = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FactorySettings, AgentSessionLimiter> ControllerLimits = new();
    private readonly IAgentRuntime _inner;
    private readonly AgentSessionLimiter _limiter;

    public SlottedAgentRuntime(IAgentRuntime inner, int maxSessions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessions);
        _inner = inner;
        _limiter = FixedLimits.GetOrAdd(maxSessions, limit => new AgentSessionLimiter(() => limit));
    }

    internal SlottedAgentRuntime(IAgentRuntime inner, FactorySettings settings)
    {
        _inner = inner;
        _limiter = ForController(settings);
    }

    private static AgentSessionLimiter ForController(FactorySettings settings) =>
        ControllerLimits.GetValue(settings, owner => new AgentSessionLimiter(() => Math.Max(1, owner.MaxAgentSessions)));

    internal static void NotifyLimitChanged(FactorySettings settings) => ForController(settings).SignalWaiters();

    Task IAgentRuntimeAdmission.VerifyAdmissionAsync(string previousSession, bool readOnly, CancellationToken cancellation) =>
        IAgentRuntimeAdmission.VerifyAsync(_inner, previousSession, readOnly, cancellation);

    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        await _limiter.EnterAsync(cancellation);
        try { return await _inner.RunAsync(directory, prompt, previousSession, cancellation, onSession, readOnly, onUsage); }
        finally { _limiter.Exit(); }
    }
}

internal sealed class AgentSessionLimiter(Func<int> capacity)
{
    private readonly object _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;

    internal async Task EnterAsync(CancellationToken cancellation)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_active < capacity())
                {
                    _active++;
                    return;
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellation);
        }
    }

    internal void Exit()
    {
        lock (_gate)
        {
            _active--;
            SignalWaiters();
        }
    }

    internal void SignalWaiters()
    {
        lock (_gate)
        {
            var changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }
    }
}
