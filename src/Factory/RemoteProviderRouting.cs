using System.Text.Json;

namespace Factory;

public sealed class RemoteProviderLease
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public long ExecutionGeneration { get; set; }
    public string WorkerId { get; set; } = "";
    public string ExecutorUrl { get; set; } = "";
    public string RouteJson { get; set; } = "";
    public int TurnTokens { get; set; }
    public int MaxJobTokens { get; set; }
}

public sealed class RemoteProviderTurn
{
    public Guid Id { get; set; }
    public Guid LeaseId { get; set; }
    public Guid UsageId { get; set; }
    public string Role { get; set; } = "";
    public string InputCommit { get; set; } = "";
    public string OutputTree { get; set; } = "";
    public string ObservedModel { get; set; } = "";
    public string GrantDigest { get; set; } = "";
    public string HoldReason { get; set; } = "";
    public string State { get; set; } = "active";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record RemoteProviderRoute(string Role, string ProviderId, string Kind, string Runtime,
    string Model, string Endpoint, long ProfileLimit, long ProjectLimit);

public sealed class RemoteProviderTurnRequest
{
    public string WorkerId { get; set; } = "";
    public Guid JobId { get; set; }
    public Guid LeaseId { get; set; }
    public Guid TurnId { get; set; }
    public long ExecutionGeneration { get; set; }
    public string LeaseToken { get; set; } = "";
    public string Role { get; set; } = "";
    public string Commit { get; set; } = "";
    public byte[] Bundle { get; set; } = [];
}

public sealed record RemoteExecutorGrant(Guid TurnId, Guid LeaseId, Guid JobId, long ExecutionGeneration,
    RemoteProviderRoute Route, string Commit, string BaseCommit, string ExpectedInputTree, string Repository,
    bool VerifyRemoteBase, int Budget, string Task, byte[] Bundle,
    IReadOnlyList<string> ExpectedPaths, IReadOnlyList<string> ProtectedPaths);

public sealed record RemoteExecutorReceipt(Guid TurnId, Guid LeaseId, string ProviderId, string Role,
    string InputCommit, string ObservedModel, long InputTokens, long OutputTokens, bool FinalUsage,
    bool Success, string FinalText, bool FinalTextIsFallback, string OutputTree, byte[] Patch,
    long? ReportedCostMicros = null);

public sealed record RemoteReceiptRecoveryInput(bool Reviewed, string Reason, bool ConfirmOverrun);

public static class RemoteProviderRouting
{
    public const string Capability = "remote-provider:v2";
    public const int ProtocolVersion = 2;
    internal const int MaxBundleBytes = 8 * 1024 * 1024;
    internal const int MaxPatchBytes = 1024 * 1024;
    internal const int MaxPromptChars = 256 * 1024;
    internal static readonly string[] Roles = ["builder", "review:code", "review:architecture", "review:security", "review:ux"];

    internal static bool Required(ProjectInfo project, FactoryJob job) =>
        project.RequiredExecutionCapabilities.Contains(Capability) || job.RequiredExecutionCapabilities.Contains(Capability);

    internal static bool IsExecutorUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)) &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/";

    internal static string? ExecutorCredential(FactorySettings settings)
    {
        var token = Environment.GetEnvironmentVariable("FACTORY_REMOTE_EXECUTOR_TOKEN");
        return WorkerAuthentication.IsValidKey(token) &&
            !string.Equals(token, settings.ApiToken, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(token, settings.WorkerTokenKey, StringComparison.OrdinalIgnoreCase) ? token : null;
    }

    internal static RemoteProviderRoute[]? Plan(FactoryState state, FactorySettings settings, FactoryJob job)
    {
        if (!settings.AllowPush || !settings.EnableRemoteProviderRouting || !IsExecutorUrl(settings.RemoteProviderExecutorUrl) ||
            ExecutorCredential(settings) is null || job.GoalId is null ||
            settings.RemoteProviderTurnTokens is < 256 or > 65536 ||
            settings.MaxJobReservationTokens < settings.GoalReservationTokens)
            return null;
        try
        {
            var project = state.Projects.Single(p => p.Id == job.ProjectId);
            GitHubWorkerResultVerifier.GitHubRepository(project.Origin);
            var routes = Roles.Select(role =>
            {
                var profile = ProviderRouter.Resolve(settings, state, job.ProjectId, role);
                var model = ProviderRouter.EffectiveModel(settings, profile, job.Risk);
                var runtime = profile.Kind == "codex" ? ProviderRouter.EffectiveCodexRuntime(settings, profile) : "";
                var limit = state.ProviderBudgetLimits.SingleOrDefault(
                    b => b.ProjectId == job.ProjectId && b.ProviderId == profile.Id)?.TokenLimit ?? 0;
                return new RemoteProviderRoute(role, profile.Id, profile.Kind, runtime, model, profile.Kind == "ollama" ? profile.Endpoint : "", profile.TokenLimit, limit);
            }).ToArray();
            return routes.All(SupportsEvidence) ? routes : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return null; }
    }

    internal static bool SupportsEvidence(RemoteProviderRoute route) =>
        Roles.Contains(route.Role, StringComparer.Ordinal) &&
        !string.IsNullOrWhiteSpace(route.Model) && route.Model.Length <= 128 &&
        (route.Kind == "codex" && route.Runtime == "app-server" ||
         route.Kind == "ollama" && route.Role != "builder" && route.Model.Contains(':') &&
         !route.Model.EndsWith(":latest", StringComparison.OrdinalIgnoreCase));

    internal static RemoteProviderLease? Current(FactoryState state, FactoryJob job) =>
        state.RemoteProviderLeases.SingleOrDefault(l => l.JobId == job.Id && l.ExecutionGeneration == job.ExecutionGeneration);

    internal static bool CanContinue(FactoryState state, FactorySettings settings, FactoryJob job, RemoteProviderLease lease)
    {
        var routes = Plan(state, settings, job);
        return routes is not null && Json.Stringify(routes) == lease.RouteJson &&
            settings.RemoteProviderExecutorUrl == lease.ExecutorUrl && settings.RemoteProviderTurnTokens == lease.TurnTokens &&
            settings.MaxJobReservationTokens == lease.MaxJobTokens;
    }

    public static async Task<RemoteExecutorReceipt> ExecuteAsync(StateStore store, FactorySettings settings,
        RemoteProviderTurnRequest request, CancellationToken ct)
    {
        if (request.TurnId == Guid.Empty || !IntegrationVerifier.IsCommitSha(request.Commit) ||
            request.Bundle is not { Length: > 0 and <= MaxBundleBytes } ||
            !Roles.Contains(request.Role, StringComparer.Ordinal))
            throw new ArgumentException("Invalid remote provider turn");

        var dispatch = store.Change(state => Begin(state, settings, request));
        try
        {
            var credential = ExecutorCredential(settings) ?? throw new InvalidOperationException("Executor credential unavailable");
            RemoteExecutorReceipt receipt;
            try { receipt = await RemoteProviderExecutor.InvokeAsync(dispatch.ExecutorUrl, credential, dispatch.Grant, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // Retry only the receipt lookup. Dispatching the same provider call again is unsafe.
                receipt = await RemoteProviderExecutor.FetchReceiptAsync(dispatch.ExecutorUrl, credential,
                    new(dispatch.Grant.TurnId, RemoteProviderExecutor.Digest(dispatch.Grant)), ct)
                    ?? throw new InvalidOperationException("Executor has no durable receipt for the interrupted turn", ex);
            }
            var accepted = store.Change((state, db, tx) =>
            {
                var job = state.Jobs.Single(j => j.Id == request.JobId);
                var previous = (job.State, job.LeaseOwner, job.ExecutionGeneration);
                var result = Record(state, settings, dispatch.Grant, receipt);
                if (job.State != previous.State)
                    StateStore.RecordLifecycle(db, tx, job.ProjectId, "job", job.Id, previous.LeaseOwner,
                        previous.State, job.State, LifecycleReason.LeaseFenced,
                        reference: request.TurnId.ToString(), generation: previous.ExecutionGeneration, state: state);
                return result;
            });
            if (!accepted) throw new InvalidOperationException("Remote provider turn requires reconciliation");
            return receipt;
        }
        catch
        {
            store.Change((state, db, tx) =>
            {
                var turn = state.RemoteProviderTurns.Single(t => t.Id == request.TurnId);
                var job = state.Jobs.Single(j => j.Id == request.JobId);
                if (turn.State == "active" && job.ExecutionGeneration == request.ExecutionGeneration)
                {
                    StateStore.RecordLifecycle(db, tx, job.ProjectId, "job", job.Id, job.LeaseOwner, job.State,
                        JobStates.Blocked, LifecycleReason.LeaseFenced, reference: turn.Id.ToString(),
                        generation: job.ExecutionGeneration, state: state);
                    Fence(state, job, "Trusted executor result was unavailable");
                }
                return 0;
            });
            throw;
        }
    }

    internal static (string ExecutorUrl, RemoteExecutorGrant Grant) Begin(FactoryState state, FactorySettings settings,
        RemoteProviderTurnRequest request)
    {
        var job = state.Jobs.Single(j => j.Id == request.JobId);
        var project = state.Projects.Single(p => p.Id == job.ProjectId);
        var worker = state.Workers.Single(w => w.Id == request.WorkerId);
        var lease = Current(state, job);
        JobRecovery.Require(job, new(request.ExecutionGeneration, request.LeaseToken, "remote:" + worker.Id));
        if (lease is null || lease.Id != request.LeaseId || lease.WorkerId != worker.Id ||
            !WorkerAuthorization.CanContinue(state, worker, project, job, settings) ||
            !CanContinue(state, settings, job, lease))
            throw new InvalidOperationException("Remote provider lease is not authorized");
        if (state.RemoteProviderTurns.Any(t => t.Id == request.TurnId))
            throw new InvalidOperationException("Remote provider turn has already been dispatched");

        var route = JsonSerializer.Deserialize<RemoteProviderRoute[]>(lease.RouteJson, Json.Options)!
            .Single(r => r.Role == request.Role);
        var turns = state.RemoteProviderTurns.Where(t => t.LeaseId == lease.Id).ToArray();
        var builder = turns.LastOrDefault(t => t.Role == "builder");
        var firstBuilder = turns.FirstOrDefault(t => t.Role == "builder");
        if ((route.Role == "builder" && turns.Any(t => t.State == "active")) ||
            (route.Role != "builder" && (builder is not { State: "passed" } ||
             turns.Any(t => t.Role == route.Role && t.State == "active"))))
            throw new InvalidOperationException("Remote provider turn is out of sequence");
        var repository = GitHubWorkerResultVerifier.GitHubRepository(project.Origin);
        var task = job.Title + "\n" + job.Description;
        if (task.Length > MaxPromptChars) throw new InvalidOperationException("Work package description is too large");
        var pending = state.RemoteProviderTurns.Where(t => t.LeaseId == lease.Id && t.State == "active")
            .Join(state.ProviderBudgetReservations, t => t.UsageId, r => r.UsageId, (_, r) => r.Reserved - r.Consumed)
            .Sum();
        var budget = (int)Math.Min(lease.TurnTokens, GoalBudgetLedger.Remaining(state, job) - pending);
        if (budget < BudgetedAgentRuntime.MinimumTurnAllowance)
            throw new TokenBudgetExceededException("No uncommitted lease tokens for this turn");
        var profile = new ProviderProfile { Id = route.ProviderId, Kind = route.Kind, TokenLimit = route.ProfileLimit };
        var usage = ProviderBudgetLedger.Begin(state, job, profile, route.Role, route.Model, budget,
            reserveUncapped: true);
        var reservation = state.ProviderBudgetReservations.Single(r => r.UsageId == usage.Id);
        // The lease, rather than a controller PID, owns a dispatched remote turn.
        reservation.OwnerProcessId = 0;
        reservation.OwnerStartedAt = null;
        reservation.OwnerMachine = "";
        var grant = new RemoteExecutorGrant(request.TurnId, lease.Id, job.Id, job.ExecutionGeneration, route,
            request.Commit, firstBuilder?.InputCommit ?? request.Commit,
            route.Role == "builder" ? builder?.OutputTree ?? "" : builder!.OutputTree,
            repository, firstBuilder is null, checked((int)reservation.Reserved), task, request.Bundle,
            job.ExpectedPaths.ToArray(), project.ProtectedPaths.ToArray());
        state.RemoteProviderTurns.Add(new RemoteProviderTurn
        {
            Id = request.TurnId, LeaseId = lease.Id, UsageId = usage.Id, Role = route.Role,
            InputCommit = request.Commit, GrantDigest = RemoteProviderExecutor.Digest(grant)
        });
        return (lease.ExecutorUrl, grant);
    }

    internal static bool CanRecoverReceipt(RemoteProviderTurn turn, RemoteProviderLease lease, FactoryJob job,
        FactorySettings settings) => turn.State == "held" && turn.GrantDigest.Length == 64 &&
        !JobStates.Active.Contains(job.State) && string.IsNullOrEmpty(job.LeaseOwner) &&
        string.Equals(settings.RemoteProviderExecutorUrl, lease.ExecutorUrl, StringComparison.Ordinal) &&
        IsExecutorUrl(lease.ExecutorUrl) && ExecutorCredential(settings) is not null;

    public static async Task<object> RecoverReceiptAsync(StateStore store, FactorySettings settings, Guid jobId,
        Guid turnId, RemoteReceiptRecoveryInput input, CancellationToken ct)
    {
        if (!input.Reviewed || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length is < 10 or > 1000)
            throw new InvalidOperationException("Receipt recovery requires an independent review and evidence reason");
        var state = store.Read();
        var turn = state.RemoteProviderTurns.Single(t => t.Id == turnId);
        var lease = state.RemoteProviderLeases.Single(l => l.Id == turn.LeaseId && l.JobId == jobId);
        if (turn.State == "reconciled") return new { turn.Id, turn.State };
        if (!CanRecoverReceipt(turn, lease, state.Jobs.Single(j => j.Id == jobId), settings))
            throw new InvalidOperationException("No held receipt from the configured executor is available");
        var credential = ExecutorCredential(settings) ?? throw new InvalidOperationException("Executor credential unavailable");
        var receipt = await RemoteProviderExecutor.FetchReceiptAsync(lease.ExecutorUrl, credential,
            new(turn.Id, turn.GrantDigest), ct) ?? throw new InvalidOperationException("Executor has no durable receipt for this turn");
        return SettleRecoveredReceipt(store, jobId, turnId, input, receipt);
    }

    internal static object SettleRecoveredReceipt(StateStore store, Guid jobId, Guid turnId,
        RemoteReceiptRecoveryInput input, RemoteExecutorReceipt receipt)
    {
        var state = store.Read();
        var turn = state.RemoteProviderTurns.Single(t => t.Id == turnId);
        var lease = state.RemoteProviderLeases.Single(l => l.Id == turn.LeaseId && l.JobId == jobId);
        if (turn.State == "reconciled") return new { turn.Id, turn.State };
        var usage = state.ProviderUsages.Single(u => u.Id == turn.UsageId);
        VerifyRecoveredReceipt(turn, lease, usage, receipt);
        var reservation = state.ProviderBudgetReservations.Single(r => r.UsageId == usage.Id);
        return ProviderBudgetReconciliation.Reconcile(store, reservation.Id, ReceiptSettlementInput(turn.Id, input, receipt),
            receipt.ObservedModel, receipt.ReportedCostMicros);
    }

    internal static void VerifyRecoveredReceipt(RemoteProviderTurn turn, RemoteProviderLease lease,
        ProviderUsageEntry usage, RemoteExecutorReceipt receipt)
    {
        if (turn.State != "held" || !receipt.FinalUsage || receipt.TurnId != turn.Id ||
            receipt.LeaseId != lease.Id || receipt.InputCommit != turn.InputCommit || receipt.Role != turn.Role ||
            receipt.ProviderId != usage.ProviderId || receipt.ObservedModel != usage.Model)
            throw new InvalidOperationException("Executor receipt does not establish final usage for this held turn");
    }

    internal static ProviderBudgetReconciliationInput ReceiptSettlementInput(Guid turnId,
        RemoteReceiptRecoveryInput input, RemoteExecutorReceipt receipt)
    {
        var reason = "Recovered executor receipt " + turnId + ". " + input.Reason.Trim();
        return new()
        {
            Reviewed = input.Reviewed, Reason = reason[..Math.Min(reason.Length, 1000)],
            ConfirmOverrun = input.ConfirmOverrun, FinalInputTokens = receipt.InputTokens, FinalOutputTokens = receipt.OutputTokens
        };
    }

    internal static bool Record(FactoryState state, FactorySettings settings, RemoteExecutorGrant grant, RemoteExecutorReceipt receipt)
    {
        var job = state.Jobs.Single(j => j.Id == grant.JobId);
        var turn = state.RemoteProviderTurns.Single(t => t.Id == grant.TurnId);
        var lease = Current(state, job);
        if (lease is null || lease.Id != grant.LeaseId || job.ExecutionGeneration != grant.ExecutionGeneration ||
            !JobStates.Active.Contains(job.State) || job.LeaseUntil is not { } deadline || deadline <= DateTimeOffset.UtcNow ||
            job.LeaseOwner != "remote:" + lease.WorkerId || turn.State != "active")
            return false;
        if (receipt.TurnId != grant.TurnId || receipt.LeaseId != grant.LeaseId ||
            receipt.ProviderId != grant.Route.ProviderId || receipt.Role != grant.Route.Role ||
            receipt.InputCommit != grant.Commit || receipt.InputTokens < 0 || receipt.OutputTokens < 0 ||
            receipt.InputTokens > long.MaxValue - receipt.OutputTokens ||
            receipt.Patch is null || receipt.Patch.Length > MaxPatchBytes ||
            (grant.Route.Role != "builder" && (receipt.Patch.Length != 0 || !string.IsNullOrEmpty(receipt.OutputTree))) ||
            receipt.FinalText is null || receipt.FinalText.Length > MaxPromptChars)
        {
            Fence(state, job, "Trusted executor receipt did not match the turn");
            return false;
        }

        var providerRemaining = ProviderBudgetLedger.Record(state, turn.UsageId,
            new(receipt.InputTokens, receipt.OutputTokens), receipt.ReportedCostMicros);
        var goalRemaining = GoalBudgetLedger.Record(state, job, checked(job.TokensIn + receipt.InputTokens),
            checked(job.TokensOut + receipt.OutputTokens));
        turn.ObservedModel = receipt.ObservedModel ?? "";
        turn.OutputTree = receipt.OutputTree ?? "";
        var project = state.Projects.Single(p => p.Id == job.ProjectId);
        var worker = state.Workers.Single(w => w.Id == lease.WorkerId);
        string? reason = null;
        if (providerRemaining < 0 || goalRemaining < 0)
            reason = "Executor reported usage above the reserved budget";
        else if (!string.IsNullOrEmpty(receipt.ObservedModel) && receipt.ObservedModel != grant.Route.Model)
            reason = "Executor reported a different model from the lease route";
        else if (!receipt.FinalUsage)
            reason = "Executor did not provide final usage";
        else if (receipt.ObservedModel != grant.Route.Model)
            reason = "Executor did not confirm the model on the lease route";
        else if (!WorkerAuthorization.CanContinue(state, worker, project, job, settings) ||
            !CanContinue(state, settings, job, lease))
            reason = "Remote provider lease authorization changed during the turn";
        else if (receipt.Success && grant.Route.Role == "builder" && !IntegrationVerifier.IsCommitSha(turn.OutputTree))
            reason = "Executor returned no valid builder tree";
        if (reason is not null)
        {
            Fence(state, job, reason);
            return false;
        }

        ProviderBudgetLedger.Finish(state, turn.UsageId, true);
        using var verdict = new StringReader(receipt.FinalText);
        turn.State = receipt.Success && (grant.Route.Role == "builder" ||
            (!receipt.FinalTextIsFallback && verdict.ReadLine() == "VERDICT: PASS")) ? "passed" : "failed";
        return true;
    }

    internal static void HoldTurns(FactoryState state, FactoryJob job, string reason = "Remote executor turn interrupted")
    {
        var lease = Current(state, job);
        if (lease is null) return;
        foreach (var turn in state.RemoteProviderTurns.Where(t => t.LeaseId == lease.Id && t.State == "active"))
        {
            turn.State = "held";
            turn.HoldReason = reason;
            ProviderBudgetLedger.Hold(state, state.ProviderBudgetReservations.Single(r => r.UsageId == turn.UsageId),
                reason);
        }
    }

    internal static void Fence(FactoryState state, FactoryJob job, string reason)
    {
        HoldTurns(state, job, reason);
        GoalBudgetLedger.Hold(state, job, reason);
        job.ExecutionGeneration = checked(job.ExecutionGeneration + 1);
        job.State = JobStates.Blocked;
        job.Stage = "reconcile";
        job.Error = reason;
        job.LeaseOwner = "";
        job.LeaseToken = "";
        job.LeaseUntil = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        state.Events.Add(new FactoryEvent { JobId = job.Id, Type = "remote_provider_reconciliation_required", Message = reason });
    }

    internal static bool VerifyCompletion(FactoryState state, RemoteProviderLease lease, string head, string tree)
    {
        var turns = state.RemoteProviderTurns.Where(t => t.LeaseId == lease.Id).ToArray();
        var builder = turns.LastOrDefault(t => t.Role == "builder");
        return builder is { State: "passed" } && builder.OutputTree == tree &&
            turns.All(t => t.State is "passed" or "failed") &&
            Roles.Where(role => role != "builder").All(role => turns.LastOrDefault(t =>
                t.Role == role && t.InputCommit == head) is { State: "passed" });
    }
}
