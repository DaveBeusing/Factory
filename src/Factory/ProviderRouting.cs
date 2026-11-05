using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory;

/// <summary>A single approved provider/model assignment for a project execution role.</summary>
public sealed class ProjectProviderRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Role { get; set; } = "";
    public string ProviderId { get; set; } = "codex";
    // Empty means the provider's configured default, not a verified model identity.
    public string Model { get; set; } = "";
}

public sealed record SelectedProvider(string Role, string ProviderId, string Model)
{
    public string ModelLabel => string.IsNullOrWhiteSpace(Model) ? "provider default (unverified)" : Model;
}

public static class ProviderRouting
{
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
    {
        "planning", "analysis", "implementation", "review",
        "review:code", "review:architecture", "review:security", "review:ux"
    };

    public static readonly string[] Providers = ["codex", "claude"];

    public static void Validate(string role, string providerId, string model)
    {
        if (!Roles.Contains(role))
            throw new ArgumentException("Unknown provider role: " + role);
        if (!Providers.Contains(providerId, StringComparer.Ordinal))
            throw new ArgumentException("Unknown provider: " + providerId);
        if (!Regex.IsMatch(model ?? "", @"^[a-zA-Z0-9._:/-]{0,128}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid model identifier");
        // Claude Code's Plan mode is deliberately not treated as a write sandbox.
        if (providerId == "claude" && role == "implementation")
            throw new InvalidOperationException("Claude Code is currently enabled only for read-only planning, analysis and review");
    }

    public static SelectedProvider Select(FactorySettings settings, IEnumerable<ProjectProviderRoute> routes,
        Guid projectId, string role, string risk = "medium")
    {
        if (!Roles.Contains(role)) throw new ArgumentException("Unknown provider role: " + role);
        var assignments = routes.Where(r => r.ProjectId == projectId).ToList();
        var assigned = assignments.FirstOrDefault(r => r.Role == role);
        if (assigned is null && role.StartsWith("review:", StringComparison.Ordinal))
            assigned = assignments.FirstOrDefault(r => r.Role == "review");
        var provider = assigned?.ProviderId ?? "codex";
        var model = assigned?.Model ?? "";
        if (provider == "codex" && model.Length == 0)
        {
            model = settings.ModelRoutes.GetValueOrDefault(risk) ?? "";
            if (model.Length == 0) model = settings.Model;
        }
        Validate(role, provider, model);
        return new SelectedProvider(role, provider, model);
    }

    public static IAgentRuntime Create(FactorySettings settings, SelectedProvider selected,
        bool remote = false)
    {
        Validate(selected.Role, selected.ProviderId, selected.Model);
        if (remote && selected.ProviderId != "codex")
            throw new InvalidOperationException("Remote worker attestation for this provider is not implemented");
        IAgentRuntime runtime = selected.ProviderId switch
        {
            "codex" => settings.Runtime == "app-server"
                ? new AppServerAgentRuntime(selected.Model)
                : new ExecAgentRuntime(selected.Model),
            "claude" => new ClaudeReadOnlyAgentRuntime(selected.Model),
            _ => throw new InvalidOperationException("Unsupported provider")
        };
        return new SlottedAgentRuntime(runtime, Math.Max(1, settings.MaxAgentSessions));
    }
}

/// <summary>
/// Claude Code print-mode adapter. Planning/reviews are read-only by contract:
/// the CLI is explicitly limited to read tools and plan mode. This is not an OS sandbox.
/// </summary>
public sealed class ClaudeReadOnlyAgentRuntime(string model) : IAgentRuntime
{
    private static long? Nonnegative(JsonElement node, string key)
    {
        return node.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number &&
            item.TryGetInt64(out var value) && value >= 0 ? value : null;
    }

    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        if (!readOnly)
            throw new InvalidOperationException("Claude adapter cannot execute write-enabled jobs");
        var args = new List<string> { "-p", "--output-format", "json",
            "--permission-mode", "plan", "--tools", "Read,Glob,Grep" };
        if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model); }
        if (!string.IsNullOrWhiteSpace(previousSession))
        {
            if (!Guid.TryParse(previousSession, out _))
                throw new ArgumentException("Invalid Claude session identifier");
            args.Add("--resume"); args.Add(previousSession);
        }
        var command = await Commands.RunAsync("claude", args, directory, 7200, prompt,
            cancellationToken: cancellation);
        var result = new AgentRunResult { Success = command.Ok };
        if (!command.Ok)
        {
            result.Error = "Claude Code CLI failed with exit code " + command.Code + ": " +
                command.Error[..Math.Min(1000, command.Error.Length)];
            return result;
        }
        using var parsed = JsonDocument.Parse(command.Output);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("result", out var message) || message.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Claude CLI returned no structured result");
        result.FinalText = message.GetString() ?? "";
        if (root.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String)
        {
            result.SessionId = session.GetString() ?? "";
            if (result.SessionId.Length > 0) onSession?.Invoke(result.SessionId);
        }
        if (root.TryGetProperty("is_error", out var errorFlag) &&
            errorFlag.ValueKind == JsonValueKind.True)
        {
            result.Success = false;
            result.Error = "Claude Code reported an execution error";
        }
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var input = Nonnegative(usage, "input_tokens");
            var output = Nonnegative(usage, "output_tokens");
            var cacheRead = Nonnegative(usage, "cache_read_input_tokens") ?? 0;
            var cacheWrite = Nonnegative(usage, "cache_creation_input_tokens") ?? 0;
            if (input is not null && output is not null)
            {
                result.InputTokens = checked(input.Value + cacheRead + cacheWrite);
                result.OutputTokens = output.Value;
                result.UsageReported = true;
                onUsage?.Invoke(new AgentTokenUsage(result.InputTokens, result.OutputTokens));
            }
        }
        if (root.TryGetProperty("total_cost_usd", out var cost) &&
            cost.ValueKind == JsonValueKind.Number && cost.TryGetDecimal(out var dollars) && dollars >= 0)
            result.ProviderReportedCostMicros = checked((long)decimal.Round(dollars * 1_000_000m));
        return result;
    }
}
