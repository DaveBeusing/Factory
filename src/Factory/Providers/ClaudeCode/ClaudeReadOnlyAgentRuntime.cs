using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory;

internal sealed class ClaudeReadOnlyAgentRuntime(string model) : IAgentRuntime, IAgentRuntimeAdmission
{
    private static readonly string[] RestrictedArguments =
    [
        "--safe-mode", "--restricted", "--print", "--output-format", "json", "--no-session-persistence",
        "--permission-mode", "plan", "--tools", "Read,Glob,Grep", "--disable-slash-commands",
        "--strict-mcp-config", "--mcp-config", "{}", "--disallowed-tools", "mcp__*", "--no-chrome",
        "--max-turns", "64"
    ];

    internal static IEnumerable<string> CapabilityProbeArguments =>
        [.. RestrictedArguments, "--model", "sonnet", "--help"];

    private static async Task<string> AdmitAsync(string previousSession, bool readOnly, CancellationToken cancellation)
    {
        if (!readOnly || !string.IsNullOrEmpty(previousSession))
            throw new InvalidOperationException("Claude Code supports only independent read-only turns");
        return await ClaudeCodeExecutable.RequireSupportedAsync(cancellation);
    }

    async Task IAgentRuntimeAdmission.VerifyAdmissionAsync(string previousSession, bool readOnly, CancellationToken cancellation) =>
        _ = await AdmitAsync(previousSession, readOnly, cancellation);

    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        var executable = await AdmitAsync(previousSession, readOnly, cancellation);
        var args = new List<string>(RestrictedArguments);
        if (model.Length > 0) { args.Add("--model"); args.Add(model); }

        var command = await Commands.RunAsync(executable, args, directory,
            timeoutSeconds: 7200, stdin: prompt, cancellationToken: cancellation);
        var result = new AgentRunResult
        {
            RequestedModel = model,
            Error = command.Ok ? "Claude Code returned no structured result" : "Claude Code exited with code " + command.Code
        };
        if (command.OutputTruncated)
        {
            result.Error = "Claude Code returned an incomplete structured result";
            return result;
        }
        try
        {
            using var document = JsonDocument.Parse(command.Output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != "result")
                return result;

            if (root.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String &&
                Guid.TryParseExact(session.GetString(), "D", out var sessionId))
                result.SessionId = sessionId.ToString("D");
            if (root.TryGetProperty("modelUsage", out var modelUsage) && modelUsage.ValueKind == JsonValueKind.Object)
            {
                var models = modelUsage.EnumerateObject().Select(entry => entry.Name).Distinct(StringComparer.Ordinal).ToArray();
                if (models.All(name => Regex.IsMatch(name, @"\A[a-zA-Z0-9._:/-]{1,128}\z")))
                {
                    result.ObservedModels = models;
                    result.ObservedModel = models.Length == 1 ? models[0] : "";
                }
            }

            if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number &&
                cost.TryGetDecimal(out var dollars) && dollars >= 0 && dollars <= long.MaxValue / 1_000_000m)
                result.ProviderReportedCostMicros = (long)decimal.Round(dollars * 1_000_000m);

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                Tokens(usage, "input_tokens") is { } input && Tokens(usage, "output_tokens") is { } output &&
                Tokens(usage, "cache_read_input_tokens", optional: true) is { } read &&
                Tokens(usage, "cache_creation_input_tokens", optional: true) is { } created &&
                (decimal)input + read + created + output <= long.MaxValue)
            {
                result.InputTokens = input + read + created;
                result.OutputTokens = output;
                result.UsageReported = true;
            }

            var outcome = root.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String
                ? subtype.GetString() : null;
            if (command.Ok && outcome == "success" &&
                root.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.False &&
                root.TryGetProperty("result", out var message) && message.ValueKind == JsonValueKind.String)
            {
                result.Success = true;
                result.FinalText = message.GetString() ?? "";
                result.Error = "";
            }
            else if (outcome == "error_max_turns")
                result.Error = "Claude Code reached its read-only turn limit";
            else if (outcome == "error_max_budget_usd")
                result.Error = "Claude Code reached its provider cost limit";
            else if (command.Ok)
                result.Error = "Claude Code reported an unsuccessful turn";
        }
        catch (JsonException) { }
        if (result.SessionId.Length > 0) onSession?.Invoke(result.SessionId);
        return result;
    }

    private static long? Tokens(JsonElement usage, string name, bool optional = false)
    {
        if (!usage.TryGetProperty(name, out var value)) return optional ? 0 : null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0
            ? count : null;
    }
}
