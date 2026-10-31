using System.Diagnostics;
using System.Text.Json;

namespace Factory;

public sealed class AgentRunResult
{
    public bool Success { get; set; }
    public string FinalText { get; set; } = "";
    public string Error { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

public interface IAgentRuntime
{
    Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false);
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
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false)
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
                    response.InputTokens += u.TryGetProperty("input_tokens", out var ti) ? ti.GetInt64() : 0;
                    response.OutputTokens += u.TryGetProperty("output_tokens", out var to) ? to.GetInt64() : 0;
                }
                if (type == "turn.failed") response.Error = line;
            }
            catch (JsonException) { events.Add(line); }
        }, cancellation);
        response.Success = command.Ok && string.IsNullOrEmpty(response.Error);
        if (!response.Success) response.Error += " " + command.Error.Trim();
        if (string.IsNullOrEmpty(response.FinalText)) response.FinalText = command.Output[^Math.Min(command.Output.Length, 12000)..];
        return response;
    }
}

public sealed class AppServerAgentRuntime(string model) : IAgentRuntime
{
    // Local stdio JSON-RPC. No experimental remote websocket transport.
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false)
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
            run.SessionId = thread.GetProperty("thread").GetProperty("id").GetString() ?? "";
            onSession?.Invoke(run.SessionId);
            await SendAsync(new { method = "turn/start", id = 3, @params = new
            {
                threadId = run.SessionId, input = new[] { new { type = "text", text = prompt } }, cwd = directory
            } });
            await ResponseAsync(3);
            while (true)
            {
                var line = await reader.ReadLineAsync(scope.Token) ?? throw new IOException("Agent app-server closed mid-turn");
                using var doc = JsonDocument.Parse(line);
                var node = doc.RootElement;
                if (node.TryGetProperty("method", out var m))
                {
                    var name = m.GetString() ?? "";
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
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false)
    {
        var semaphore = Slots.GetOrAdd(maxSessions, n => new SemaphoreSlim(n, n));
        await semaphore.WaitAsync(cancellation);
        try { return await inner.RunAsync(directory, prompt, previousSession, cancellation, onSession, readOnly); }
        finally { semaphore.Release(); }
    }
}
