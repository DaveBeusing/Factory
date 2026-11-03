namespace Factory;

public static class JobStates
{
    public const string Queued = "queued", Preparing = "preparing", Building = "building",
        Gating = "gating", Reviewing = "reviewing", Reworking = "reworking",
        AwaitingApproval = "awaiting_approval", PrReady = "pr_ready",
        CompletedLocal = "completed_local", Integrated = "integrated", Blocked = "blocked",
        Failed = "failed", Paused = "paused";
    public const string Stopped = "stopped";
    public static readonly HashSet<string> Active = [Preparing, Building, Gating, Reviewing, Reworking, AwaitingApproval];
}

public sealed class FactoryState
{
    public int Schema { get; set; } = 1;
    public List<ProjectInfo> Projects { get; set; } = [];
    public List<FactoryJob> Jobs { get; set; } = [];
    public List<FactoryGoal> Goals { get; set; } = [];
    public List<FactoryEvent> Events { get; set; } = [];
    public List<KnowledgeItem> Knowledge { get; set; } = [];
    public List<GraphNode> Graph { get; set; } = [];
    public List<EvaluationResult> Evaluations { get; set; } = [];
    public List<ReleaseInfo> Releases { get; set; } = [];
    public List<WorkerInfo> Workers { get; set; } = [];
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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
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

public sealed class FactorySettings
{
    public int MaxParallel { get; set; } = 3;
    public int MaxAgentSessions { get; set; } = 6;
    public int LeaseSeconds { get; set; } = 120;
    public int MaxReviewRounds { get; set; } = 3;
    public string Runtime { get; set; } = "exec";
    public string Model { get; set; } = "";
    public Dictionary<string, string> ModelRoutes { get; set; } = new()
    {
        ["low"] = "", ["medium"] = "", ["high"] = ""
    };
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
