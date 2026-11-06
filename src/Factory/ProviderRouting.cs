using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Factory;

public sealed class ProviderProfile
{
    public string Id { get; set; } = "codex";
    public string Kind { get; set; } = "codex";
    public string Model { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    public bool Enabled { get; set; } = true;
    // A Factory-owned total token ceiling. Zero means no local provider ceiling.
    // This is NOT a provider's account entitlement or remaining allowance.
    public long TokenLimit { get; set; }
}

public sealed class ProviderAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Role { get; set; } = "";
    public string ProviderId { get; set; } = "";
}

public sealed class ProviderBudgetLimit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string ProviderId { get; set; } = "";
    public long TokenLimit { get; set; }
}

public sealed class ProviderUsageEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? GoalId { get; set; }
    public Guid? JobId { get; set; }
    public long ExecutionGeneration { get; set; }
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Role { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public bool FinalUsageReported { get; set; }
    public long? ReportedCostMicros { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProviderBudgetReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UsageId { get; set; }
    public Guid ProjectId { get; set; }
    public string ProviderId { get; set; } = "";
    public long Reserved { get; set; }
    public long Consumed { get; set; }
    public int OwnerProcessId { get; set; }
    public string OwnerMachine { get; set; } = "";
    public DateTimeOffset? OwnerStartedAt { get; set; }
    // active, held, released. Unknown final usage keeps the unspent liability held.
    public string State { get; set; } = "active";
}

public readonly record struct ProviderSelection(string Id, string Model, string Role, IAgentRuntime Runtime);

public static class ProviderRouter
{
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    { "analysis", "planner", "builder", "review", "review:code", "review:architecture",
      "review:security", "review:ux" };
    public static bool ValidRole(string role) => Roles.Contains(role);

    public static ProviderProfile Resolve(FactorySettings settings, FactoryState state,
        Guid projectId, string role)
    {
        if (!ValidRole(role)) throw new ArgumentException("Unknown provider role", nameof(role));
        if (!state.Projects.Any(p => p.Id == projectId))
            throw new InvalidOperationException("Project does not exist");
        var selection = state.ProviderAssignments.FirstOrDefault(x => x.ProjectId == projectId && x.Role == role)
            ?? (role.StartsWith("review:", StringComparison.Ordinal)
                ? state.ProviderAssignments.FirstOrDefault(x => x.ProjectId == projectId && x.Role == "review") : null);
        var id = selection?.ProviderId ?? "codex";
        var matches = settings.Providers.Where(x => x.Id == id).ToArray();
        if (matches.Length != 1 || !matches[0].Enabled)
            throw new InvalidOperationException("Configured provider is missing or disabled: " + id);
        var profile = matches[0];
        Validate(profile, role);
        return profile;
    }

    public static void Validate(ProviderProfile profile, string role)
    {
        if (!ValidRole(role)) throw new ArgumentException("Unknown provider role");
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 64 ||
            profile.TokenLimit < 0) throw new ArgumentException("Invalid provider definition");
        switch (profile.Kind)
        {
            case "codex":
                if (!string.IsNullOrWhiteSpace(profile.Runtime) &&
                    profile.Runtime is not ("exec" or "app-server"))
                    throw new ArgumentException("Unsupported Codex runtime");
                break;
            case "claude":
                if (role == "builder")
                    throw new InvalidOperationException("Claude Code supports only read-only planning, analysis and reviews");
                if (profile.Model is null || !System.Text.RegularExpressions.Regex.IsMatch(
                    profile.Model, @"\A[a-zA-Z0-9._:/-]{0,128}\z") || profile.Model.StartsWith('-'))
                    throw new ArgumentException("Invalid Claude Code model identifier");
                break;
            case "ollama":
                if (role == "builder")
                    throw new InvalidOperationException("Ollama text-only adapter cannot modify files for builder jobs");
                if (string.IsNullOrWhiteSpace(profile.Model))
                    throw new ArgumentException("Ollama requires an explicit model");
                if (!Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var url) ||
                    !url.IsLoopback || url.Scheme != Uri.UriSchemeHttp ||
                    !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) ||
                    !string.IsNullOrEmpty(url.Fragment))
                    throw new InvalidOperationException("Ollama endpoint must be an unauthenticated loopback HTTP URL");
                break;
            default: throw new InvalidOperationException("Unsupported provider kind: " + profile.Kind);
        }
    }

    internal static string EffectiveCodexRuntime(FactorySettings settings, ProviderProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Runtime) ? settings.Runtime : profile.Runtime;

    internal static string EffectiveModel(FactorySettings settings, ProviderProfile profile, string risk) =>
        profile.Kind == "codex" && string.IsNullOrWhiteSpace(profile.Model)
            ? (settings.ModelRoutes.GetValueOrDefault(risk) is { Length: > 0 } model ? model : settings.Model)
            : profile.Model;

    public static ProviderSelection Select(FactorySettings settings, FactoryState state,
        Guid projectId, string role, string risk)
    {
        var profile = Resolve(settings, state, projectId, role);
        var model = EffectiveModel(settings, profile, risk);
        IAgentRuntime adapter = profile.Kind switch
        {
            "codex" => EffectiveCodexRuntime(settings, profile)
                == "app-server" ? new AppServerAgentRuntime(model) : new ExecAgentRuntime(model),
            "ollama" => new OllamaAgentRuntime(profile.Endpoint, model),
            "claude" => new ClaudeReadOnlyAgentRuntime(model),
            _ => throw new InvalidOperationException("Unknown provider adapter")
        };
        return new(profile.Id, model, role,
            new SlottedAgentRuntime(adapter, Math.Max(1, settings.MaxAgentSessions)));
    }
}

/// <summary>
/// Text-only adapter. Prompts must already contain the bounded repository context.
/// No tool access, file modification or session continuation is offered.
/// </summary>
public sealed class OllamaAgentRuntime(string endpoint, string model) : IAgentRuntime
{
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        if (!readOnly) throw new InvalidOperationException("Ollama adapter supports read-only work only");
        if (!string.IsNullOrEmpty(previousSession))
            throw new InvalidOperationException("Ollama adapter does not support session continuation");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        using var result = await client.PostAsJsonAsync(new Uri(new Uri(endpoint.TrimEnd('/') + "/"), "api/generate"),
            new { model, prompt, stream = false }, cancellation);
        result.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await result.Content.ReadAsStreamAsync(cancellation),
            cancellationToken: cancellation);
        var root = doc.RootElement;
        var response = new AgentRunResult
        {
            ObservedModel = root.TryGetProperty("model", out var reportedModel) && reportedModel.ValueKind == JsonValueKind.String
                ? reportedModel.GetString() ?? "" : "",
            Success = root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True,
            FinalText = root.TryGetProperty("response", out var text) ? text.GetString() ?? "" : ""
        };
        if (root.TryGetProperty("prompt_eval_count", out var input) &&
            root.TryGetProperty("eval_count", out var output) &&
            input.TryGetInt64(out var inTokens) && output.TryGetInt64(out var outTokens) &&
            inTokens >= 0 && outTokens >= 0)
        {
            response.InputTokens = inTokens;
            response.OutputTokens = outTokens;
            response.UsageReported = response.Success;
            if (response.UsageReported) onUsage?.Invoke(new(inTokens, outTokens));
        }
        if (!response.Success) response.Error = "Ollama did not report a completed response";
        return response;
    }
}

public static class ProviderBudgetLedger
{
    public static ProviderUsageEntry Begin(FactoryState state, FactoryJob job, ProviderProfile profile,
        string role, string model, int reservationTokens, bool reserveUncapped = false)
    {
        var usage = new ProviderUsageEntry { ProjectId = job.ProjectId, GoalId = job.GoalId,
            JobId = job.Id == Guid.Empty ? null : job.Id, ExecutionGeneration = job.ExecutionGeneration,
            ProviderId = profile.Id, Model = model, Role = role };
        var projectCap = state.ProviderBudgetLimits.FirstOrDefault(b =>
            b.ProjectId == job.ProjectId && b.ProviderId == profile.Id)?.TokenLimit ?? 0;
        var globalCap = profile.TokenLimit;
        var isPlanning = job.Id == Guid.Empty && job.GoalId.HasValue;
        long allowed = 0;
        if (globalCap > 0 || projectCap > 0 || isPlanning || reserveUncapped)
        {
            long Used(Func<ProviderUsageEntry, bool> filter) =>
                state.ProviderUsages.Where(filter).Sum(x => checked(x.InputTokens + x.OutputTokens));
            long Pending(Func<ProviderBudgetReservation, bool> filter) =>
                state.ProviderBudgetReservations.Where(x => x.State != "released" && filter(x))
                    .Sum(x => Math.Max(0, checked(x.Reserved - x.Consumed)));
            allowed = reservationTokens;
            if (isPlanning)
                allowed = Math.Min(allowed, GoalBudgetLedger.Available(state, job.GoalId!.Value));
            if (globalCap > 0) allowed = Math.Min(allowed,
                globalCap - Used(x => x.ProviderId == profile.Id) -
                Pending(x => x.ProviderId == profile.Id));
            if (projectCap > 0) allowed = Math.Min(allowed,
                projectCap - Used(x => x.ProviderId == profile.Id && x.ProjectId == job.ProjectId) -
                Pending(x => x.ProviderId == profile.Id && x.ProjectId == job.ProjectId));
            if (allowed < BudgetedAgentRuntime.MinimumTurnAllowance)
                throw new TokenBudgetExceededException("Provider budget does not allow another reserved turn");
        }
        // A goal-funded turn needs an auditable recovery record even when the provider
        // has no local ceiling. A zero reservation never limits provider execution.
        if (allowed > 0 || job.GoalId.HasValue)
        {
            using var owner = Process.GetCurrentProcess();
            state.ProviderBudgetReservations.Add(new ProviderBudgetReservation
            {
                UsageId = usage.Id, ProjectId = job.ProjectId, ProviderId = profile.Id, Reserved = allowed,
                OwnerProcessId = owner.Id, OwnerMachine = Environment.MachineName,
                OwnerStartedAt = new DateTimeOffset(owner.StartTime.ToUniversalTime())
            });
        }
        state.ProviderUsages.Add(usage);
        return usage;
    }

    public static void HoldOrphans(FactoryState state)
    {
        foreach (var reservation in state.ProviderBudgetReservations.Where(r => r.State == "active"))
        {
            if (!OwnerStopped(reservation)) continue;
            Hold(state, reservation, "Owner process stopped");
        }
    }

    internal static bool HasForeignOwner(ProviderBudgetReservation reservation) =>
        !string.IsNullOrWhiteSpace(reservation.OwnerMachine) &&
        !string.Equals(reservation.OwnerMachine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    internal static void Hold(FactoryState state, ProviderBudgetReservation reservation, string reason)
    {
        reservation.State = "held";
        var usage = state.ProviderUsages.Single(u => u.Id == reservation.UsageId);
        state.Events.Add(new FactoryEvent { JobId = usage.JobId, Type = "provider_budget_held",
            Message = "Provider reservation " + reservation.Id + " requires verification. " + reason });
    }

    private static bool OwnerStopped(ProviderBudgetReservation reservation)
    {
        if (reservation.OwnerProcessId <= 0 || reservation.OwnerStartedAt is null ||
            !reservation.OwnerMachine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using var process = Process.GetProcessById(reservation.OwnerProcessId);
            return process.HasExited ||
                new DateTimeOffset(process.StartTime.ToUniversalTime()) != reservation.OwnerStartedAt;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    public static long Record(FactoryState state, Guid usageId, AgentTokenUsage total)
    {
        var entry = state.ProviderUsages.Single(x => x.Id == usageId);
        var reservation = state.ProviderBudgetReservations.FirstOrDefault(x => x.UsageId == usageId);
        if (reservation is not null && reservation.State != "active")
            throw new LostExecutionLeaseException("Provider reservation is no longer active");
        if (total.InputTokens < entry.InputTokens || total.OutputTokens < entry.OutputTokens)
            return long.MaxValue;
        entry.InputTokens = total.InputTokens;
        entry.OutputTokens = total.OutputTokens;
        if (reservation is null) return long.MaxValue;
        reservation.Consumed = checked(total.InputTokens + total.OutputTokens);
        return reservation.Reserved > 0 ? reservation.Reserved - reservation.Consumed : long.MaxValue;
    }

    public static void Finish(FactoryState state, Guid usageId, bool finalReported, long? reportedCostMicros = null)
    {
        var entry = state.ProviderUsages.Single(x => x.Id == usageId);
        var reservation = state.ProviderBudgetReservations.FirstOrDefault(x => x.UsageId == usageId);
        if (reservation is not null && reservation.State != "active") return;
        entry.FinalUsageReported = finalReported;
        if (reportedCostMicros is >= 0) entry.ReportedCostMicros = reportedCostMicros;
        if (reservation is not null) reservation.State = finalReported ? "released" : "held";
    }

    private static decimal? ReportedCostUsd(IEnumerable<ProviderUsageEntry> usage)
    {
        decimal? total = null;
        foreach (var entry in usage)
            if (entry.ReportedCostMicros is { } micros) total = (total ?? 0) + micros / 1_000_000m;
        return total;
    }

    public static object Summary(FactoryState state, FactorySettings settings) =>
        settings.Providers.Select(profile =>
        {
            var records = state.ProviderUsages.Where(u => u.ProviderId == profile.Id).ToArray();
            var pending = state.ProviderBudgetReservations.Where(r => r.ProviderId == profile.Id &&
                r.State != "released").Sum(r => Math.Max(0, checked(r.Reserved - r.Consumed)));
            var spent = records.Sum(x => checked(x.InputTokens + x.OutputTokens));
            return new { profile.Id, profile.Kind, profile.Model, profile.Enabled,
                inputTokens = records.Sum(x => x.InputTokens), outputTokens = records.Sum(x => x.OutputTokens),
                observedTokens = spent, reservedTokens = pending, reportedCostUsd = ReportedCostUsd(records),
                models = records.GroupBy(x => x.Model).Select(modelGroup => new
                {
                    model = string.IsNullOrWhiteSpace(modelGroup.Key) ? "Provider default" : modelGroup.Key,
                    inputTokens = modelGroup.Sum(x => x.InputTokens),
                    outputTokens = modelGroup.Sum(x => x.OutputTokens),
                    roles = modelGroup.GroupBy(x => x.Role).Select(roleGroup => new
                    {
                        role = roleGroup.Key,
                        inputTokens = roleGroup.Sum(x => x.InputTokens),
                        outputTokens = roleGroup.Sum(x => x.OutputTokens)
                    }).ToArray()
                }).ToArray(),
                localRemainingTokens = profile.TokenLimit > 0 ? (long?)Math.Max(0, profile.TokenLimit - spent - pending) : null,
                accountRemaining = (double?)null, // Account entitlements are never inferred.
                projects = state.Projects.Select(p =>
                {
                    var limit = state.ProviderBudgetLimits.FirstOrDefault(b =>
                        b.ProjectId == p.Id && b.ProviderId == profile.Id)?.TokenLimit ?? 0;
                    var projectRecords = records.Where(x => x.ProjectId == p.Id).ToArray();
                    var projectSpent = projectRecords.Sum(x => checked(x.InputTokens + x.OutputTokens));
                    var projectPending = state.ProviderBudgetReservations.Where(r =>
                        r.ProviderId == profile.Id && r.ProjectId == p.Id && r.State != "released")
                        .Sum(r => Math.Max(0, checked(r.Reserved - r.Consumed)));
                    return new { projectId = p.Id, p.Name, configuredLimit = limit,
                        inputTokens = projectRecords.Sum(x => x.InputTokens), outputTokens = projectRecords.Sum(x => x.OutputTokens),
                        observedTokens = projectSpent, reservedTokens = projectPending,
                        reportedCostUsd = ReportedCostUsd(projectRecords),
                        remainingTokens = limit > 0 ? (long?)Math.Max(0, limit - projectSpent - projectPending) : null };
                }).ToArray()
            };
        }).ToArray();
}

public sealed class ProjectProvidersInput
{
    public Dictionary<string, string> Routes { get; set; } = new();
    // Zero or absent removes the local project ceiling for a provider profile.
    public Dictionary<string, long> Budgets { get; set; } = new();
}

public static class ProjectProviderManager
{
    public static object Configure(StateStore store, FactorySettings settings, Guid projectId,
        ProjectProvidersInput input)
    {
        if (input.Routes is null || input.Budgets is null)
            throw new ArgumentException("Provider routing and budgets must be objects");
        foreach (var (role, providerId) in input.Routes)
        {
            if (!ProviderRouter.ValidRole(role))
                throw new ArgumentException("Unsupported role: " + role);
            var profile = settings.Providers.SingleOrDefault(x => x.Id == providerId && x.Enabled)
                ?? throw new ArgumentException("Unknown or disabled provider: " + providerId);
            ProviderRouter.Validate(profile, role);
        }
        foreach (var (id, limit) in input.Budgets)
        {
            if (limit < 0 || !settings.Providers.Any(x => x.Id == id && x.Enabled))
                throw new ArgumentException("Invalid provider budget for " + id);
        }
        return store.Change(state =>
        {
            var project = state.Projects.Single(p => p.Id == projectId);
            if (state.Jobs.Any(j => j.ProjectId == projectId && JobStates.Active.Contains(j.State)))
                throw new InvalidOperationException("Pause and reconcile running project jobs before changing provider assignments");
            foreach (var (id, cap) in input.Budgets)
            {
                if (cap == 0) continue;
                var used = state.ProviderUsages.Where(u => u.ProjectId == projectId && u.ProviderId == id)
                    .Sum(u => checked(u.InputTokens + u.OutputTokens));
                var outstanding = state.ProviderBudgetReservations.Where(x =>
                    x.ProjectId == projectId && x.ProviderId == id && x.State != "released")
                    .Sum(x => Math.Max(0, x.Reserved - x.Consumed));
                if (cap < checked(used + outstanding))
                    throw new InvalidOperationException("Budget cannot be lower than consumed and reserved capacity");
            }
            state.ProviderAssignments.RemoveAll(x => x.ProjectId == projectId);
            state.ProviderBudgetLimits.RemoveAll(x => x.ProjectId == projectId);
            state.ProviderAssignments.AddRange(input.Routes.Select(x => new ProviderAssignment
                { ProjectId = projectId, Role = x.Key, ProviderId = x.Value }));
            state.ProviderBudgetLimits.AddRange(input.Budgets.Where(x => x.Value > 0)
                .Select(x => new ProviderBudgetLimit
                    { ProjectId = projectId, ProviderId = x.Key, TokenLimit = x.Value }));
            state.Events.Add(new FactoryEvent { Type = "provider_configuration_updated",
                Message = "Project provider settings changed: " + project.Name });
            return new { projectId, routes = input.Routes, budgets = input.Budgets };
        });
    }
}

public sealed class ProviderBudgetReconciliationInput
{
    public long FinalInputTokens { get; set; }
    public long FinalOutputTokens { get; set; }
    public bool ConfirmOverrun { get; set; }
    public bool Reviewed { get; set; }
    public string Reason { get; set; } = "";
}

public static class ProviderBudgetReconciliation
{
    public static object HoldAfterHostRecovery(StateStore store, Guid reservationId, bool reviewed, string reason)
    {
        var evidence = EvidenceReason(reviewed, reason);
        return store.Change(state =>
        {
            var reservation = state.ProviderBudgetReservations.Single(x => x.Id == reservationId);
            if (reservation.State != "active" || !ProviderBudgetLedger.HasForeignOwner(reservation))
                throw new InvalidOperationException("Only active reservations owned by another host can use this recovery action");
            var usage = state.ProviderUsages.Single(x => x.Id == reservation.UsageId);
            RequireStoppedJob(state, usage);
            ProviderBudgetLedger.Hold(state, reservation, "Operator verified source host " +
                reservation.OwnerMachine + " stopped: " + evidence);
            return new { reservation.Id, reservation.State };
        });
    }

    private static string EvidenceReason(bool reviewed, string reason)
    {
        if (!reviewed || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 10 or > 1000)
            throw new InvalidOperationException("Verified evidence and a reason of 10 to 1000 characters are required");
        return reason.Trim();
    }

    private static FactoryJob? RequireStoppedJob(FactoryState state, ProviderUsageEntry usage)
    {
        if (usage.JobId is not { } id) return null;
        var job = state.Jobs.Single(j => j.Id == id);
        if (!string.IsNullOrEmpty(job.LeaseOwner) || JobStates.Active.Contains(job.State))
            throw new InvalidOperationException("Recover or stop the associated job before reconciling provider usage");
        return job;
    }

    public static object Reconcile(StateStore store, Guid reservationId, ProviderBudgetReconciliationInput input) =>
        Reconcile(store, reservationId, input, observedModel: null);

    internal static object Reconcile(StateStore store, Guid reservationId, ProviderBudgetReconciliationInput input,
        string? observedModel)
    {
        var evidence = EvidenceReason(input.Reviewed, input.Reason);
        if (input.FinalInputTokens < 0 || input.FinalOutputTokens < 0)
            throw new InvalidOperationException("Verified token totals must be nonnegative");
        var consumed = checked(input.FinalInputTokens + input.FinalOutputTokens);
        return store.Change(state =>
        {
            var reservation = state.ProviderBudgetReservations.Single(x => x.Id == reservationId);
            if (reservation.State != "held")
                throw new InvalidOperationException("Only held provider reservations can be reconciled");
            var usage = state.ProviderUsages.Single(x => x.Id == reservation.UsageId);
            if (input.FinalInputTokens < usage.InputTokens || input.FinalOutputTokens < usage.OutputTokens)
                throw new InvalidOperationException("Verified usage cannot be lower than already reported usage");
            var job = RequireStoppedJob(state, usage);
            if (reservation.Reserved > 0)
                GoalBudgetLedger.RequireVerifiedOverrun(reservation.Reserved, consumed, input.ConfirmOverrun);
            var goalReservation = job is null ? null : GoalBudgetLedger.ApplyProviderReconciliation(state, job, usage,
                input.FinalInputTokens - usage.InputTokens, input.FinalOutputTokens - usage.OutputTokens, input.ConfirmOverrun);
            usage.InputTokens = input.FinalInputTokens;
            usage.OutputTokens = input.FinalOutputTokens;
            usage.FinalUsageReported = true;
            reservation.Consumed = consumed;
            reservation.State = "released";
            var remoteTurn = state.RemoteProviderTurns.SingleOrDefault(turn => turn.UsageId == usage.Id);
            if (remoteTurn is { State: "held" })
            {
                remoteTurn.State = "reconciled";
                if (observedModel is not null) remoteTurn.ObservedModel = observedModel;
            }
            state.Events.Add(new FactoryEvent { JobId = usage.JobId,
                Type = "provider_budget_reconciled",
                Message = "Provider " + usage.ProviderId + " usage " + usage.Id +
                    (goalReservation is null ? "" : " linked goal reservation " + goalReservation.Id) +
                    (reservation.Reserved > 0 && consumed > reservation.Reserved
                        ? "; verified provider overrun " + (consumed - reservation.Reserved) : "") + ": " + evidence });
            return new { reservation.Id, reservation.State, usage.ProviderId,
                usage.InputTokens, usage.OutputTokens, goalReservationId = goalReservation?.Id };
        });
    }
}

public sealed class ProjectScopedAgentRuntime(IAgentRuntime inner, StateStore store, Guid projectId,
    Guid? goalId, ProviderProfile profile, string role, string model, int reservationTokens) : IAgentRuntime
{
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        if (!readOnly)
            throw new InvalidOperationException("Project-scoped analysis and planning must be read-only");
        store.Change(ProviderBudgetLedger.HoldOrphans);
        var usageId = store.Change(state =>
        {
            if (!state.Projects.Any(p => p.Id == projectId && p.Status == "active"))
                throw new InvalidOperationException("Project is unavailable");
            if (goalId.HasValue && !state.Goals.Any(g => g.Id == goalId && g.ProjectId == projectId && g.State == "draft"))
                throw new InvalidOperationException("Planning requires a draft goal");
            if (goalId.HasValue && state.ProviderBudgetReservations.Any(r => r.State == "active" &&
                state.ProviderUsages.Any(u => u.Id == r.UsageId && u.GoalId == goalId && !u.JobId.HasValue)))
                throw new InvalidOperationException("A planning turn is already active for this goal");
            return ProviderBudgetLedger.Begin(state,
                new FactoryJob { Id = Guid.Empty, ProjectId = projectId, GoalId = goalId },
                profile, role, model, reservationTokens).Id;
        });
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        bool finalUsage = false;
        long? reportedCostMicros = null;
        bool limitReached = false;
        long lastIn = 0, lastOut = 0;
        void Observe(AgentTokenUsage usage)
        {
            if (usage.InputTokens < lastIn || usage.OutputTokens < lastOut) return;
            var remaining = store.Change(state => ProviderBudgetLedger.Record(state, usageId, usage));
            lastIn = usage.InputTokens; lastOut = usage.OutputTokens;
            onUsage?.Invoke(usage);
            if (remaining <= 0) { limitReached = true; scope.Cancel(); }
        }
        try
        {
            var result = await inner.RunAsync(directory, prompt, previousSession, scope.Token,
                onSession, readOnly: true, onUsage: Observe);
            reportedCostMicros = result.ProviderReportedCostMicros;
            if (result.UsageReported)
            {
                Observe(new AgentTokenUsage(result.InputTokens, result.OutputTokens));
                finalUsage = result.InputTokens == lastIn && result.OutputTokens == lastOut;
            }
            if (limitReached) throw new TokenBudgetExceededException("Project provider reservation exhausted");
            if (!finalUsage)
                throw new TokenBudgetExceededException("Planning provider did not report final token usage");
            return result;
        }
        catch (OperationCanceledException) when (limitReached && !cancellation.IsCancellationRequested)
        {
            throw new TokenBudgetExceededException("Project provider reservation exhausted");
        }
        finally
        {
            store.Change(state => ProviderBudgetLedger.Finish(state, usageId, finalUsage, reportedCostMicros));
        }
    }
}
