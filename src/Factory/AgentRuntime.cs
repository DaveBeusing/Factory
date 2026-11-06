using System.Diagnostics;
using System.Text.Json;

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

/// <summary>Deduplicates cumulative app-server usage notifications, including resumed threads.</summary>
public sealed class AppServerTokenTracker
{
    private AgentTokenUsage? _lastThreadTotal;
    private AgentTokenUsage _turn;
    public AgentTokenUsage Turn => _turn;
    public bool Reported { get; private set; }

    private static bool TryTokens(JsonElement node, out AgentTokenUsage usage)
    {
        usage = default;
        if (node.ValueKind != JsonValueKind.Object) return false;
        static long? Token(JsonElement data, string camel, string snake)
        {
            if (!data.TryGetProperty(camel, out var value) && !data.TryGetProperty(snake, out value)) return null;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;
        }
        var input = Token(node, "inputTokens", "input_tokens");
        var output = Token(node, "outputTokens", "output_tokens");
        if (input is null || output is null) return false;
        usage = new(input.Value, output.Value);
        return true;
    }

    public bool Observe(JsonElement notification)
    {
        if (!notification.TryGetProperty("method", out var method) ||
            method.GetString() != "thread/tokenUsage/updated" ||
            !notification.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("tokenUsage", out var tokenUsage) ||
            !tokenUsage.TryGetProperty("total", out var totalNode) ||
            !TryTokens(totalNode, out var total)) return false;
        if (_lastThreadTotal is null)
        {
            // A resumed thread's total includes earlier turns; charge only its latest increment.
            if (!tokenUsage.TryGetProperty("last", out var last) || !TryTokens(last, out var delta))
                return false; // Never attribute an unknown preexisting thread total to this turn.
            _turn = delta;
        }
        else
        {
            var previous = _lastThreadTotal.Value;
            // Ignore repeated/replayed or regressing snapshots.
            if (total.InputTokens < previous.InputTokens || total.OutputTokens < previous.OutputTokens)
                return false;
            _turn = new(checked(_turn.InputTokens + total.InputTokens - previous.InputTokens),
                        checked(_turn.OutputTokens + total.OutputTokens - previous.OutputTokens));
        }
        _lastThreadTotal = total;
        Reported = true;
        return true;
    }
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

public sealed class ExecAgentRuntime(string model) : IAgentRuntime
{
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        var args = new List<string> { "exec", "--json", "--sandbox", readOnly ? "read-only" : "workspace-write", "--cd", directory };
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model); }
        if (!string.IsNullOrEmpty(previousSession)) { args.Add("resume"); args.Add(previousSession); }
        args.Add("-");
        var response = new AgentRunResult();
        var events = new List<string>();
        var command = await Commands.RunAsync("codex", args, directory, 7200, prompt, line =>
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                if (type == "thread.started" && root.TryGetProperty("thread_id", out var thread))
                {
                    response.SessionId = thread.GetString() ?? "";
                    if (!string.IsNullOrEmpty(response.SessionId)) onSession?.Invoke(response.SessionId);
                }
                if (type == "item.completed" && root.TryGetProperty("item", out var item) &&
                    item.TryGetProperty("type", out var itemType) && itemType.GetString() == "agent_message" &&
                    item.TryGetProperty("text", out var text)) response.FinalText = text.GetString() ?? "";
                if (type == "turn.completed" && root.TryGetProperty("usage", out var u))
                {
                    var input = u.TryGetProperty("input_tokens", out var ti) ? ti.GetInt64() : 0;
                    var output = u.TryGetProperty("output_tokens", out var to) ? to.GetInt64() : 0;
                    if (input < 0 || output < 0) throw new InvalidDataException("Invalid usage report");
                    response.InputTokens = checked(response.InputTokens + input);
                    response.OutputTokens = checked(response.OutputTokens + output);
                    response.UsageReported = true;
                    onUsage?.Invoke(new(response.InputTokens, response.OutputTokens));
                }
                if (type == "turn.failed") response.Error = line;
            }
            catch (JsonException) { events.Add(line); }
        }, cancellation);
        response.Success = command.Ok && string.IsNullOrEmpty(response.Error);
        if (!response.Success) response.Error += " " + command.Error.Trim();
        if (string.IsNullOrEmpty(response.FinalText))
        {
            response.FinalText = command.Output[^Math.Min(command.Output.Length, 12000)..];
            response.FinalTextIsFallback = true;
        }
        return response;
    }
}

public sealed class AppServerAgentRuntime(string model, bool verifyModel = false) : IAgentRuntime
{
    // Local stdio JSON-RPC. No experimental remote websocket transport.
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        using var process = new Process();
        process.StartInfo = Commands.StartInfo("codex", ["app-server"], directory);
        process.Start();
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        scope.CancelAfter(TimeSpan.FromHours(2));
        _ = Task.Run(async () =>
        {
            try { await process.StandardError.ReadToEndAsync(scope.Token); } catch { }
        }, CancellationToken.None);
        var reader = process.StandardOutput;
        var writer = process.StandardInput;
        async Task SendAsync(object data)
        {
            await writer.WriteLineAsync(Json.Stringify(data).Replace("\r", "").Replace("\n", ""));
            await writer.FlushAsync(scope.Token);
        }
        async Task<JsonElement> ResponseAsync(int id)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(scope.Token) ?? throw new IOException("Agent app-server closed");
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                if (r.TryGetProperty("method", out var method) && r.TryGetProperty("id", out var pendingId))
                {
                    // Unattended workers never accept an approval request implicitly.
                    await SendAsync(new { id = pendingId.Clone(), result = new { decision = "decline" } });
                    continue;
                }
                if (!r.TryGetProperty("id", out var itemId) || !itemId.TryGetInt32(out var got) || got != id) continue;
                if (r.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
                return r.GetProperty("result").Clone();
            }
        }
        var run = new AgentRunResult();
        var usageTracker = new AppServerTokenTracker();
        try
        {
            await SendAsync(new { method = "initialize", id = 1, @params = new
            {
                clientInfo = new { name = "factory", title = "Factory", version = "1.0" }
            } });
            await ResponseAsync(1);
            await SendAsync(new { method = "initialized", @params = new { } });
            if (string.IsNullOrEmpty(previousSession))
            {
                var threadParams = new Dictionary<string, object?>
                {
                    ["cwd"] = directory, ["approvalPolicy"] = "never", ["sandbox"] = readOnly ? "readOnly" : "workspaceWrite"
                };
                if (!string.IsNullOrEmpty(model)) threadParams["model"] = model;
                await SendAsync(new { method = "thread/start", id = 2, @params = threadParams });
            }
            else
                await SendAsync(new { method = "thread/resume", id = 2, @params = new { threadId = previousSession } });
            var thread = await ResponseAsync(2);
            run.ObservedModel = thread.TryGetProperty("model", out var actualModel) && actualModel.ValueKind == JsonValueKind.String
                ? actualModel.GetString() ?? "" : "";
            if (verifyModel && run.ObservedModel != model)
            {
                run.Error = "App-server did not confirm the requested model";
                return run;
            }
            run.SessionId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
            onSession?.Invoke(run.SessionId);
            await SendAsync(new { method = "turn/start", id = 3, @params = new
            {
                threadId = run.SessionId, input = new[] { new { type = "text", text = prompt } }, cwd = directory
            } });
            var turnStart = await ResponseAsync(3);
            var turnId = turnStart.TryGetProperty("turn", out var startedTurn) &&
                startedTurn.TryGetProperty("id", out var startedId) ? startedId.GetString() : null;
            while (true)
            {
                var line = await reader.ReadLineAsync(scope.Token) ?? throw new IOException("Agent app-server closed mid-turn");
                using var doc = JsonDocument.Parse(line);
                var node = doc.RootElement;
                if (node.TryGetProperty("method", out var m))
                {
                    var name = m.GetString() ?? "";
                    if (name == "thread/tokenUsage/updated" &&
                        (turnId == null || (node.TryGetProperty("params", out var usageParams) &&
                         usageParams.TryGetProperty("turnId", out var usageTurnId) &&
                         usageTurnId.GetString() == turnId)) && usageTracker.Observe(node))
                    {
                        var usage = usageTracker.Turn;
                        run.InputTokens = usage.InputTokens;
                        run.OutputTokens = usage.OutputTokens;
                        run.UsageReported = true;
                        onUsage?.Invoke(usage);
                    }
                    if (node.TryGetProperty("id", out var pending))
                    {
                        await SendAsync(new { id = pending.Clone(), result = new { decision = "decline" } });
                        continue;
                    }
                    if (name == "item/completed" && node.TryGetProperty("params", out var pars) &&
                        pars.TryGetProperty("item", out var item) && item.TryGetProperty("type", out var type) &&
                        type.GetString() == "agentMessage" && item.TryGetProperty("text", out var txt))
                        run.FinalText = txt.GetString() ?? "";
                    if (name == "turn/completed")
                    {
                        var status = node.GetProperty("params").GetProperty("turn").GetProperty("status").GetString();
                        run.Success = status == "completed";
                        if (!run.Success) run.Error = "Agent turn status: " + status;
                        break;
                    }
                }
            }
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        return run;
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
