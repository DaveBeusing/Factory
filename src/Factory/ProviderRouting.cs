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
    public Guid JobId { get; set; }
    public long ExecutionGeneration { get; set; }
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Role { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public bool FinalUsageReported { get; set; }
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

    public static ProviderSelection Select(FactorySettings settings, FactoryState state,
        Guid projectId, string role, string risk)
    {
        var profile = Resolve(settings, state, projectId, role);
        var model = profile.Model;
        if (profile.Kind == "codex" && string.IsNullOrWhiteSpace(model))
            model = settings.ModelRoutes.GetValueOrDefault(risk) is { Length: > 0 } route ? route : settings.Model;
        IAgentRuntime adapter = profile.Kind switch
        {
            "codex" => (string.IsNullOrWhiteSpace(profile.Runtime) ? settings.Runtime : profile.Runtime)
                == "app-server" ? new AppServerAgentRuntime(model) : new ExecAgentRuntime(model),
            "ollama" => new OllamaAgentRuntime(profile.Endpoint, model),
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
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var result = await client.PostAsJsonAsync(new Uri(new Uri(endpoint.TrimEnd('/') + "/"), "api/generate"),
            new { model, prompt, stream = false }, cancellation);
        result.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await result.Content.ReadAsStreamAsync(cancellation),
            cancellationToken: cancellation);
        var root = doc.RootElement;
        var response = new AgentRunResult
        {
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
        string role, string model, int reservationTokens)
    {
        var usage = new ProviderUsageEntry { ProjectId = job.ProjectId, GoalId = job.GoalId,
            JobId = job.Id, ExecutionGeneration = job.ExecutionGeneration,
            ProviderId = profile.Id, Model = model, Role = role };
        var projectCap = state.ProviderBudgetLimits.FirstOrDefault(b =>
            b.ProjectId == job.ProjectId && b.ProviderId == profile.Id)?.TokenLimit ?? 0;
        var globalCap = profile.TokenLimit;
        if (globalCap > 0 || projectCap > 0)
        {
            long Used(Func<ProviderUsageEntry, bool> filter) =>
                state.ProviderUsages.Where(filter).Sum(x => checked(x.InputTokens + x.OutputTokens));
            long Pending(Func<ProviderBudgetReservation, bool> filter) =>
                state.ProviderBudgetReservations.Where(x => x.State != "released" && filter(x))
                    .Sum(x => checked(x.Reserved - x.Consumed));
            var allowed = (long)reservationTokens;
            if (globalCap > 0) allowed = Math.Min(allowed,
                globalCap - Used(x => x.ProviderId == profile.Id) -
                Pending(x => x.ProviderId == profile.Id));
            if (projectCap > 0) allowed = Math.Min(allowed,
                projectCap - Used(x => x.ProviderId == profile.Id && x.ProjectId == job.ProjectId) -
                Pending(x => x.ProviderId == profile.Id && x.ProjectId == job.ProjectId));
            if (allowed < BudgetedAgentRuntime.MinimumTurnAllowance)
                throw new TokenBudgetExceededException("Provider budget does not allow another reserved turn");
            state.ProviderBudgetReservations.Add(new ProviderBudgetReservation
            { UsageId = usage.Id, ProjectId = job.ProjectId, ProviderId = profile.Id, Reserved = allowed });
        }
        state.ProviderUsages.Add(usage);
        return usage;
    }

    public static long Record(FactoryState state, Guid usageId, AgentTokenUsage total)
    {
        var entry = state.ProviderUsages.Single(x => x.Id == usageId);
        if (total.InputTokens < entry.InputTokens || total.OutputTokens < entry.OutputTokens)
            return long.MaxValue;
        entry.InputTokens = total.InputTokens;
        entry.OutputTokens = total.OutputTokens;
        var reservation = state.ProviderBudgetReservations.FirstOrDefault(x => x.UsageId == usageId);
        if (reservation == null) return long.MaxValue;
        reservation.Consumed = checked(total.InputTokens + total.OutputTokens);
        return reservation.Reserved - reservation.Consumed;
    }

    public static void Finish(FactoryState state, Guid usageId, bool finalReported)
    {
        var entry = state.ProviderUsages.Single(x => x.Id == usageId);
        entry.FinalUsageReported = finalReported;
        var reservation = state.ProviderBudgetReservations.FirstOrDefault(x => x.UsageId == usageId);
        if (reservation != null) reservation.State = finalReported ? "released" : "held";
    }

    public static object Summary(FactoryState state, FactorySettings settings) =>
        settings.Providers.Select(profile =>
        {
            var records = state.ProviderUsages.Where(u => u.ProviderId == profile.Id).ToArray();
            var pending = state.ProviderBudgetReservations.Where(r => r.ProviderId == profile.Id &&
                r.State != "released").Sum(r => checked(r.Reserved - r.Consumed));
            var spent = records.Sum(x => checked(x.InputTokens + x.OutputTokens));
            return new { profile.Id, profile.Kind, profile.Model, profile.Enabled,
                inputTokens = records.Sum(x => x.InputTokens), outputTokens = records.Sum(x => x.OutputTokens),
                observedTokens = spent, reservedTokens = pending,
                localRemainingTokens = profile.TokenLimit > 0 ? (long?)Math.Max(0, profile.TokenLimit - spent - pending) : null,
                accountRemaining = (double?)null, // Account entitlements are never inferred.
                projects = state.Projects.Select(p =>
                {
                    var limit = state.ProviderBudgetLimits.FirstOrDefault(b =>
                        b.ProjectId == p.Id && b.ProviderId == profile.Id)?.TokenLimit ?? 0;
                    var projectSpent = records.Where(x => x.ProjectId == p.Id)
                        .Sum(x => checked(x.InputTokens + x.OutputTokens));
                    var projectPending = state.ProviderBudgetReservations.Where(r =>
                        r.ProviderId == profile.Id && r.ProjectId == p.Id && r.State != "released")
                        .Sum(r => checked(r.Reserved - r.Consumed));
                    return new { projectId = p.Id, p.Name, configuredLimit = limit,
                        observedTokens = projectSpent, reservedTokens = projectPending,
                        remainingTokens = limit > 0 ? (long?)Math.Max(0, limit - projectSpent - projectPending) : null };
                }).ToArray()
            };
        }).ToArray();
}
