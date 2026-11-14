namespace Factory;

public static class JobStates
{
    public const string Queued = "queued", Preparing = "preparing", Building = "building",
        Gating = "gating", Reviewing = "reviewing", Reworking = "reworking",
        AwaitingApproval = "awaiting_approval", PrReady = "pr_ready",
        CompletedLocal = "completed_local", Integrated = "integrated", Blocked = "blocked",
        Failed = "failed", Paused = "paused";
    public const string Stopped = "stopped", Cancelled = "cancelled";
    public static readonly HashSet<string> Active = [Preparing, Building, Gating, Reviewing, Reworking, AwaitingApproval];
}

internal enum LifecycleReason
{
    ExecutionAdvanced,
    ExecutionBlocked,
    ExecutionFailed,
    ExecutionPaused,
    ExecutionStopped,
    PullRequestPrepared,
    ExecutionCancelled,
    LeaseClaimed,
    LeaseFenced,
    RemoteResultVerified,
    RemoteResultRejected,
    CancelRequested,
    PauseRequested,
    StopRequested,
    OperatorPaused,
    OperatorStopped,
    OperatorResumed,
    OperatorRetried,
    Reconciled,
    IntegrationVerified,
    PlanProposed,
    PlanSuperseded,
    SpecificationSuperseded,
    PlanAdopted,
    ApprovalRequested,
    ApprovalSuperseded,
    ApprovalGranted,
    AwaitingManualIntegration
}

internal static class JobLifecycle
{
    internal static string ControlRevision(Guid id, long generation, string state, string request, DateTimeOffset updatedAt) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"{id:D}\n{generation}\n{state}\n{request}\n{updatedAt.UtcTicks}"))));

    internal static LifecycleReason ControlReason(string action) => action switch
    {
        "pause" => LifecycleReason.OperatorPaused,
        "stop" => LifecycleReason.OperatorStopped,
        "cancel" => LifecycleReason.ExecutionCancelled,
        "resume" => LifecycleReason.OperatorResumed,
        "retry" => LifecycleReason.OperatorRetried,
        _ => throw new InvalidOperationException("Unknown job control.")
    };

    internal static LifecycleReason FencedTransition(string from, string to) => (from, to) switch
    {
        (_, JobStates.Blocked) => LifecycleReason.ExecutionBlocked,
        (_, JobStates.Failed) => LifecycleReason.ExecutionFailed,
        (_, JobStates.Paused) => LifecycleReason.ExecutionPaused,
        (_, JobStates.Stopped) => LifecycleReason.ExecutionStopped,
        (_, JobStates.Cancelled) => LifecycleReason.ExecutionCancelled,
        (JobStates.AwaitingApproval, JobStates.PrReady) => LifecycleReason.PullRequestPrepared,
        (JobStates.AwaitingApproval, JobStates.CompletedLocal) => LifecycleReason.AwaitingManualIntegration,
        (JobStates.Preparing, JobStates.Building) or
        (JobStates.Building or JobStates.Reworking, JobStates.Gating) or
        (JobStates.Gating, JobStates.Gating or JobStates.Reviewing or JobStates.Reworking) or
        (JobStates.Reviewing, JobStates.Reworking or JobStates.AwaitingApproval) or
        (JobStates.AwaitingApproval, JobStates.AwaitingApproval) => LifecycleReason.ExecutionAdvanced,
        _ => throw new InvalidOperationException($"Illegal fenced job transition: {from} → {to}")
    };
}


public sealed class FactoryState
{
    public int Schema { get; set; } = 1;
    public List<ProjectInfo> Projects { get; set; } = [];
    public List<FactoryJob> Jobs { get; set; } = [];
    public List<GoalBudgetReservation> BudgetReservations { get; set; } = [];
    public List<GoalBudgetExtension> BudgetExtensions { get; set; } = [];
    public List<FactoryGoal> Goals { get; set; } = [];
    public List<ProviderAssignment> ProviderAssignments { get; set; } = [];
    public List<ProviderBudgetLimit> ProviderBudgetLimits { get; set; } = [];
    public List<ProviderUsageEntry> ProviderUsages { get; set; } = [];
    public List<ProviderBudgetReservation> ProviderBudgetReservations { get; set; } = [];
    public List<RemoteProviderLease> RemoteProviderLeases { get; set; } = [];
    public List<RemoteProviderTurn> RemoteProviderTurns { get; set; } = [];
    public List<FactoryEvent> Events { get; set; } = [];
    public List<KnowledgeItem> Knowledge { get; set; } = [];
    public List<GraphNode> Graph { get; set; } = [];
    public List<EvaluationResult> Evaluations { get; set; } = [];
    public List<ReleaseInfo> Releases { get; set; } = [];
    public List<WorkerInfo> Workers { get; set; } = [];
    public List<WorkerProjectGrant> WorkerProjectGrants { get; set; } = [];
    public List<WorkerCapabilityProof> WorkerCapabilityProofs { get; set; } = [];
    public bool Paused { get; set; }
}

public sealed class ProjectInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Origin { get; set; } = "";
    public string BaseBranch { get; set; } = "master";
    public int MaxParallel { get; set; } = 2;
    public string Status { get; set; } = "active";
    public List<CheckCommand> Checks { get; set; } = [];
    // Additional execution requirements are controller-owned, never supplied by a worker.
    public List<string> RequiredExecutionCapabilities { get; set; } = [];
    public List<string> ProtectedPaths { get; set; } = [".github/workflows/", ".factory/", ".agents/", "factory/"];
}

public sealed class CheckCommand
{
    public string Name { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string ContainerImage { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 1200;
}

public sealed class FactoryJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? GoalId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Mode { get; set; } = "local";
    public string State { get; set; } = JobStates.Queued;
    public string Stage { get; set; } = "queue";
    public string Risk { get; set; } = "medium";
    public int Priority { get; set; } = 50;
    public int Attempt { get; set; }
    public int MaxRounds { get; set; } = 3;
    public List<Guid> DependsOn { get; set; } = [];
    public List<string> ExpectedPaths { get; set; } = [];
    public List<string> SharedResources { get; set; } = [];
    public List<string> RequiredExecutionCapabilities { get; set; } = [];
    public string Branch { get; set; } = "";
    public string Worktree { get; set; } = "";
    public string PrUrl { get; set; } = "";
    public string Error { get; set; } = "";
    public string RequestedState { get; set; } = "";
    public string SessionId { get; set; } = "";
    // Persisted fencing identity: monotonically increasing generation plus per-claim secret.
    public long ExecutionGeneration { get; set; }
    public string LeaseToken { get; set; } = "";
    public int WorkerProcessId { get; set; }
    public string WorkerMachineName { get; set; } = "";
    public DateTimeOffset? WorkerProcessStartedAt { get; set; }
    // Durable last known point, written before external side effects.
    public string CheckpointStage { get; set; } = "";
    public string CheckpointCommit { get; set; } = "";
    public DateTimeOffset? CheckpointAt { get; set; }
    public string LeaseOwner { get; set; } = "";
    public DateTimeOffset? LeaseUntil { get; set; }
    public bool Integrated { get; set; }
    public string HeadSha { get; set; } = "";
    public string EvidenceFile { get; set; } = "";
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    // Only a confirmed final provider usage report permits releasing unused funds.
    public bool UsageComplete { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public string ControlRevision => JobLifecycle.ControlRevision(Id, ExecutionGeneration, State, RequestedState, UpdatedAt);
}

/// <summary>
/// Durable controller-authorized reservation extension. This is separate from
/// the bounded operational event feed and contains no lease credentials.
/// </summary>
public sealed class GoalBudgetExtension
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GoalId { get; set; }
    public Guid JobId { get; set; }
    public Guid ReservationId { get; set; }
    public long ExecutionGeneration { get; set; }
    public string WorkerId { get; set; } = "";
    public long Granted { get; set; }
    public long AuthorizedTotal { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GoalBudgetReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GoalId { get; set; }
    public Guid JobId { get; set; }
    public long ExecutionGeneration { get; set; }
    public string LeaseToken { get; set; } = "";
    public long BaseUsage { get; set; }
    public long Reserved { get; set; }
    public long Consumed { get; set; }
    public string State { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FactoryGoal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Objective { get; set; } = "";
    public List<string> AcceptanceCriteria { get; set; } = [];
    public long TokenBudget { get; set; } = 100000;
    public string State { get; set; } = "draft";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FactoryEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid? JobId { get; set; }
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class KnowledgeItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Topic { get; set; } = "";
    public string Content { get; set; } = "";
    public string SourceSha { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GraphNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string File { get; set; } = "";
    public List<string> Imports { get; set; } = [];
    public List<string> Symbols { get; set; } = [];
    public string Hash { get; set; } = "";
}

public sealed class EvaluationResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Suite { get; set; } = "";
    public int Passed { get; set; }
    public int Total { get; set; }
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public List<string> Failures { get; set; } = [];
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ReleaseInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Version { get; set; } = "";
    public string Commit { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkerInfo
{
    public string Id { get; set; } = "";
    public DateTimeOffset Heartbeat { get; set; } = DateTimeOffset.MinValue;
    public string Platform { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Draining { get; set; }
}

// Worker identity is deliberately separate from the controller-managed authorization records.
public sealed class WorkerProjectGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkerId { get; set; } = "";
    public Guid ProjectId { get; set; }
}

public sealed class WorkerCapabilityProof
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkerId { get; set; } = "";
    public string Capability { get; set; } = "";
    // Evidence must be assessed and recorded by the controller administrator.
    public string Evidence { get; set; } = "";
    public DateTimeOffset VerifiedAt { get; set; }
}

public sealed class FactorySettings
{
    public int MaxParallel { get; set; } = 3;
    // Initial per-job reservation; total goal capacity remains authoritative.
    public int GoalReservationTokens { get; set; } = GoalBudgetLedger.DefaultReservationTokens;
    // Extensions are controller-local only until remote authorization is implemented.
    public int GoalExtensionIncrementTokens { get; set; } = 4096;
    public int GoalExtensionThresholdTokens { get; set; } = 1024;
    public int MaxJobReservationTokens { get; set; } = 65536;
    public int MaxAgentSessions { get; set; } = 6;
    public int LeaseSeconds { get; set; } = 120;
    public int MaxReviewRounds { get; set; } = 3;
    public string Runtime { get; set; } = "exec";
    // Explicit provider profiles. Only Codex is enabled by default.
    public List<ProviderProfile> Providers { get; set; } = [new()];
    public string Model { get; set; } = "";
    public Dictionary<string, string> ModelRoutes { get; set; } = new()
    {
        ["low"] = "", ["medium"] = "", ["high"] = ""
    };
    public bool EnableRemoteProviderRouting { get; set; }
    public string RemoteProviderExecutorUrl { get; set; } = "";
    public bool EnableRemoteProviderExecutor { get; set; }
    public int RemoteProviderTurnTokens { get; set; } = 1024;
    public bool AllowPush { get; set; }
    public bool AutoMerge { get; set; }
    // Administrator credential. Never pass it to worker processes or API responses.
    public string ApiToken { get; set; } = "";
    // Controller-only HMAC key for distinct, worker-scoped credentials.
    public string WorkerTokenKey { get; set; } = "";
    public bool EnsureCredentials()
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(ApiToken))
        {
            ApiToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            changed = true;
        }
        if (!WorkerAuthentication.IsValidKey(WorkerTokenKey) ||
            string.Equals(WorkerTokenKey, ApiToken, StringComparison.Ordinal))
        {
            WorkerTokenKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            changed = true;
        }
        return changed;
    }
    public string WorkspaceRoot { get; set; } = "";
    public int PollSeconds { get; set; } = 3;
    public static FactorySettings Load(string path) => File.Exists(path)
        ? System.Text.Json.JsonSerializer.Deserialize<FactorySettings>(File.ReadAllText(path), Json.Options) ?? new()
        : new();
}

public static class Json
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true
    };
    public static string Stringify<T>(T input) => System.Text.Json.JsonSerializer.Serialize(input, Options);
}
