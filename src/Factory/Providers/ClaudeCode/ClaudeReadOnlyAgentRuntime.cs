using System.Text.Json;

namespace Factory;

internal sealed class ClaudeReadOnlyAgentRuntime(string model) : IAgentRuntime
{
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        if (!readOnly || !string.IsNullOrEmpty(previousSession))
            throw new InvalidOperationException("Claude Code supports only independent read-only turns");

        var args = new List<string>
        {
            "--safe-mode", "--restricted", "--print", "--output-format", "json", "--no-session-persistence",
            "--permission-mode", "plan", "--tools", "Read,Glob,Grep", "--disable-slash-commands",
            "--strict-mcp-config", "--mcp-config", "{}", "--disallowed-tools", "mcp__*", "--no-chrome"
        };
        if (model.Length > 0) { args.Add("--model"); args.Add(model); }

        var command = await Commands.RunAsync(ClaudeCodeExecutable.Resolve(), args, directory,
            timeoutSeconds: 7200, stdin: prompt, cancellationToken: cancellation);
        var result = new AgentRunResult
        {
            Error = command.Ok ? "Claude Code returned no structured result" : "Claude Code exited with code " + command.Code
        };
        try
        {
            using var document = JsonDocument.Parse(command.Output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != "result")
                return result;

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

            if (command.Ok && root.TryGetProperty("subtype", out var subtype) &&
                subtype.ValueKind == JsonValueKind.String && subtype.GetString() == "success" &&
                root.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.False &&
                root.TryGetProperty("result", out var message) && message.ValueKind == JsonValueKind.String)
            {
                result.Success = true;
                result.FinalText = message.GetString() ?? "";
                result.Error = "";
            }
            else if (command.Ok)
                result.Error = "Claude Code reported an unsuccessful turn";
        }
        catch (JsonException) { }
        return result;
    }

    private static long? Tokens(JsonElement usage, string name, bool optional = false)
    {
        if (!usage.TryGetProperty(name, out var value)) return optional ? 0 : null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0
            ? count : null;
    }
}
