using System.Diagnostics;
using System.Text.Json;

namespace Factory;

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
        if (CodexProtocol.Text(notification, "method") != "thread/tokenUsage/updated" ||
            !notification.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("tokenUsage", out var tokenUsage) ||
            tokenUsage.ValueKind != JsonValueKind.Object ||
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


public sealed class ExecAgentRuntime(string model) : IAgentRuntime
{
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        // The CLI cannot attest the effective policy of a resumed session before executing it.
        // Rework starts a new session in the existing, fenced worktree.
        var args = new List<string> { "exec", "--json", "--sandbox", readOnly ? "read-only" : "workspace-write",
            "--cd", directory, "-c", "approval_policy=\"never\"" };
        if (!string.IsNullOrEmpty(model)) { args.Add("--model"); args.Add(model); }
        args.Add("-");
        var turn = new CodexExecTurn(onSession, onUsage);
        var command = await Commands.RunAsync("codex", args, directory, 7200, prompt, turn.Observe, cancellation);
        return turn.Finish(command.Ok, readOnly);
    }
}

internal sealed class CodexExecTurn(Action<string>? onSession = null, Action<AgentTokenUsage>? onUsage = null)
{
    private readonly AgentRunResult _result = new();
    private bool _completed;
    private bool _failed;
    private bool _invalid;
    private bool _usageObserved;
    private const int MaxMessageChars = 2_000_000;

    internal void Observe(string line)
    {
        if (line.Length > MaxMessageChars) { _invalid = true; return; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { _invalid = true; return; }
        using (doc)
        {
            var root = doc.RootElement;
            switch (CodexProtocol.Text(root, "type"))
            {
                case "thread.started" when !_completed && !_failed:
                    var session = CodexProtocol.Text(root, "thread_id");
                    if (string.IsNullOrWhiteSpace(session) ||
                        (_result.SessionId.Length > 0 && _result.SessionId != session))
                        _invalid = true;
                    else if (_result.SessionId.Length == 0)
                    {
                        _result.SessionId = session;
                        onSession?.Invoke(session);
                    }
                    break;
                case "item.completed" when !_completed && !_failed:
                    if (root.TryGetProperty("item", out var item) && CodexProtocol.Text(item, "type") == "agent_message")
                        _result.FinalText = CodexProtocol.Text(item, "text") ?? "";
                    break;
                case "turn.completed":
                    if (_completed || _failed) { _invalid = true; break; }
                    _completed = true;
                    if (root.TryGetProperty("usage", out var usage) && TryTokens(usage, "input_tokens", out var input) &&
                        TryTokens(usage, "output_tokens", out var output) && input <= long.MaxValue - output)
                    {
                        _result.InputTokens = input;
                        _result.OutputTokens = output;
                        _usageObserved = true;
                        onUsage?.Invoke(new(input, output));
                    }
                    break;
                case "turn.failed":
                    if (_completed || _failed) _invalid = true;
                    _failed = true;
                    break;
                case null:
                    _invalid = true;
                    break;
            }
        }
    }

    internal AgentRunResult Finish(bool processSucceeded, bool requiresText)
    {
        _result.UsageReported = processSucceeded && _completed && !_failed && !_invalid && _usageObserved;
        _result.Success = processSucceeded && _completed && !_failed && !_invalid &&
            (!requiresText || !string.IsNullOrWhiteSpace(_result.FinalText));
        if (!_result.Success)
        {
            _result.Error = _invalid ? "Codex returned invalid or contradictory structured events" :
                _failed ? "Codex reported a failed turn" : !_completed ? "Codex returned no completed turn" :
                !processSucceeded ? "Codex exited unsuccessfully" : "Codex returned no structured agent message";
        }
        return _result;
    }

    private static bool TryTokens(JsonElement node, string name, out long count)
    {
        count = 0;
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out count) && count >= 0;
    }
}

public sealed class AppServerAgentRuntime(string model, bool verifyModel = false) : IAgentRuntime
{
    // Only this runtime instance can authorize continuity for a thread it started
    // and completed. Older persisted IDs fall back to a fresh fenced turn.
    private sealed record Session(string ThreadId, string Directory, string Model, string Provider);
    private Session? _session;

    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        using var process = new Process();
        process.StartInfo = Commands.StartInfo("codex", ["app-server"], directory);
        process.Start();
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        scope.CancelAfter(TimeSpan.FromHours(2));
        // Drain diagnostics without collecting them.
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, scope.Token);
        try
        {
            return await RunProtocolAsync(process.StandardOutput.BaseStream, process.StandardInput, directory, prompt,
                previousSession, scope.Token, onSession, readOnly, onUsage);
        }
        catch (OperationCanceledException ex) when (scope.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("Codex app-server exceeded its turn deadline.", ex);
        }
        finally
        {
            scope.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await stderr; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    internal async Task<AgentRunResult> RunProtocolAsync(Stream reader, TextWriter writer,
        string directory, string prompt, string previousSession, CancellationToken ct,
        Action<string>? onSession = null, bool readOnly = false, Action<AgentTokenUsage>? onUsage = null)
    {
        var protocol = new CodexProtocol(reader, writer, ct);
        await protocol.SendAsync(new { method = "initialize", id = 1, @params = new
        {
            clientInfo = new { name = "factory", title = "Factory", version = "1.0" }
        } });
        await protocol.WaitForResponseAsync(1);
        await protocol.SendAsync(new { method = "initialized", @params = new { } });

        var session = _session;
        var resume = !readOnly && session is not null && previousSession == session.ThreadId &&
            GitOperations.SamePath(session.Directory, directory);
        // A failed or interrupted turn must not keep its previous resume binding.
        if (!readOnly) _session = null;
        var threadParameters = new Dictionary<string, object?>
        {
            ["cwd"] = directory, ["approvalPolicy"] = "never", ["sandbox"] = readOnly ? "read-only" : "workspace-write"
        };
        if (!string.IsNullOrEmpty(model)) threadParameters["model"] = model;
        if (resume)
        {
            threadParameters["threadId"] = session!.ThreadId;
            threadParameters["model"] = session.Model;
            threadParameters["modelProvider"] = session.Provider;
            threadParameters["excludeTurns"] = true;
        }
        await protocol.SendAsync(new { method = resume ? "thread/resume" : "thread/start", id = 2, @params = threadParameters });
        var thread = await protocol.WaitForResponseAsync(2);
        var observedModel = CodexProtocol.Text(thread, "model") ?? "";
        var provider = CodexProtocol.Text(thread, "modelProvider") ?? "";
        var threadId = thread.TryGetProperty("thread", out var threadInfo) ? CodexProtocol.Text(threadInfo, "id") : null;
        if (string.IsNullOrWhiteSpace(threadId) || !IsExpectedPolicy(thread, directory, readOnly) ||
            (verifyModel && observedModel != model) ||
            (resume && (threadId != session!.ThreadId || observedModel != session.Model || provider != session.Provider)))
            return new() { ObservedModel = observedModel, Error = "Codex did not confirm the expected thread, route or execution policy" };

        var run = new AgentRunResult { ObservedModel = observedModel, SessionId = threadId };
        onSession?.Invoke(run.SessionId);
        await protocol.SendAsync(new { method = "turn/start", id = 3, @params = new
        {
            threadId, input = new[] { new { type = "text", text = prompt } }, cwd = directory
        } });
        var early = new Queue<JsonElement>();
        var turnStart = await protocol.WaitForResponseAsync(3, threadId, early);
        var turnId = turnStart.TryGetProperty("turn", out var startedTurn) ? CodexProtocol.Text(startedTurn, "id") : null;
        if (string.IsNullOrWhiteSpace(turnId))
            throw new InvalidDataException("Codex did not confirm the started turn identity");

        var usageTracker = new AppServerTokenTracker();
        while (true)
        {
            var message = early.Count > 0 ? early.Dequeue() : await protocol.ReadNotificationAsync();
            if (!CodexProtocol.TryTurnEvent(message, threadId, out var name, out var parameters, out var eventTurn) ||
                eventTurn != turnId) continue;
            if (name == "thread/tokenUsage/updated" && usageTracker.Observe(message))
            {
                var usage = usageTracker.Turn;
                run.InputTokens = usage.InputTokens;
                run.OutputTokens = usage.OutputTokens;
                run.UsageReported = true;
                onUsage?.Invoke(usage);
            }
            else if (name == "item/completed" && parameters.TryGetProperty("item", out var item) &&
                CodexProtocol.Text(item, "type") == "agentMessage")
                run.FinalText = CodexProtocol.Text(item, "text") ?? "";
            else if (name == "turn/completed")
            {
                var status = CodexProtocol.Text(parameters.GetProperty("turn"), "status");
                run.Success = status == "completed" && (!readOnly || !string.IsNullOrWhiteSpace(run.FinalText));
                if (!run.Success) run.Error = status == "completed" ? "Codex returned no structured agent message" :
                    "Codex turn did not complete successfully";
                if (run.Success && !readOnly && observedModel.Length > 0 && provider.Length > 0)
                    _session = new(threadId, directory, observedModel, provider);
                return run;
            }
        }
    }

    private static bool IsExpectedPolicy(JsonElement thread, string directory, bool readOnly)
    {
        var cwd = CodexProtocol.Text(thread, "cwd");
        if (cwd is null || !Path.IsPathFullyQualified(cwd) || !GitOperations.SamePath(cwd, directory) ||
            CodexProtocol.Text(thread, "approvalPolicy") != "never" ||
            !thread.TryGetProperty("sandbox", out var sandbox) ||
            CodexProtocol.Text(sandbox, "type") != (readOnly ? "readOnly" : "workspaceWrite") ||
            (sandbox.TryGetProperty("networkAccess", out var network) && network.ValueKind != JsonValueKind.False))
            return false;
        return !sandbox.TryGetProperty("writableRoots", out var roots) ||
            (roots.ValueKind == JsonValueKind.Array && roots.EnumerateArray().All(root =>
                !readOnly && root.ValueKind == JsonValueKind.String && root.GetString() is { } path &&
                Path.IsPathFullyQualified(path) && GitOperations.SamePath(path, directory)));
    }
}

internal sealed class CodexProtocol(Stream reader, TextWriter writer, CancellationToken ct)
{
    private const int MaxMessageBytes = 2_000_000;
    private const int MaxBufferedChars = 2_000_000;
    private const int MaxBufferedEvents = 64;
    private readonly byte[] _readBuffer = new byte[16 * 1024];
    private int _readOffset;
    private int _readCount;

    internal async Task SendAsync(object message)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(message, Json.Options).Replace("\r", "").Replace("\n", "").AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    internal async Task<JsonElement> WaitForResponseAsync(int id, string? threadId = null, Queue<JsonElement>? early = null)
    {
        var bufferedChars = 0;
        while (true)
        {
            var message = await ReadAsync();
            if (await HandleServerRequestAsync(message)) continue;
            if (message.TryGetProperty("id", out var itemId))
            {
                if (itemId.ValueKind != JsonValueKind.Number || !itemId.TryGetInt32(out var got) || got != id) continue;
                if (message.TryGetProperty("error", out _))
                    throw new InvalidDataException("Codex app-server rejected request " + id);
                if (!message.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Codex app-server returned no result for request " + id);
                return result;
            }
            if (early is not null && threadId is not null && TryTurnEvent(message, threadId, out _, out _, out _))
            {
                bufferedChars += message.GetRawText().Length;
                if (early.Count >= MaxBufferedEvents || bufferedChars > MaxBufferedChars)
                    throw new InvalidDataException("Codex app-server exceeded the early notification limit");
                early.Enqueue(message);
            }
        }
    }

    internal async Task<JsonElement> ReadNotificationAsync()
    {
        while (true)
        {
            var message = await ReadAsync();
            if (!await HandleServerRequestAsync(message) && !message.TryGetProperty("id", out _))
                return message;
        }
    }

    private async Task<JsonElement> ReadAsync()
    {
        var line = await ReadLineAsync();
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Codex app-server protocol message must be an object");
            return document.RootElement.Clone();
        }
        catch (JsonException ex) { throw new InvalidDataException("Codex app-server returned invalid JSON", ex); }
    }

    private async Task<ReadOnlyMemory<byte>> ReadLineAsync()
    {
        MemoryStream? pending = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_readOffset == _readCount)
            {
                _readOffset = 0;
                _readCount = await reader.ReadAsync(_readBuffer.AsMemory(), ct);
                if (_readCount == 0)
                    throw new IOException("Codex app-server closed before completing the turn");
            }

            var newline = Array.IndexOf(_readBuffer, (byte)'\n', _readOffset, _readCount - _readOffset);
            var end = newline < 0 ? _readCount : newline;
            var length = end - _readOffset;
            if (length > MaxMessageBytes - (pending?.Length ?? 0))
                throw new InvalidDataException("Codex app-server protocol message exceeded size limit");
            if (newline >= 0 && pending is null)
            {
                var line = _readBuffer.AsMemory(_readOffset, length);
                _readOffset = end + 1;
                return line;
            }
            pending ??= new();
            pending.Write(_readBuffer, _readOffset, length);
            _readOffset = newline < 0 ? end : end + 1;
            if (newline >= 0) return pending.GetBuffer().AsMemory(0, (int)pending.Length);
        }
    }

    private async Task<bool> HandleServerRequestAsync(JsonElement request)
    {
        var method = Text(request, "method");
        if (method is null || !request.TryGetProperty("id", out var id)) return false;
        if (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
            await SendAsync(new { id, result = new { decision = "decline" } });
        else
            await SendAsync(new { id, error = new { code = -32601, message = "Factory does not handle interactive requests" } });
        return true;
    }

    internal static bool TryTurnEvent(JsonElement message, string threadId, out string? name,
        out JsonElement parameters, out string? turnId)
    {
        name = Text(message, "method");
        parameters = default;
        turnId = null;
        if (name is not ("thread/tokenUsage/updated" or "item/completed" or "turn/completed") ||
            !message.TryGetProperty("params", out parameters) || Text(parameters, "threadId") != threadId) return false;
        turnId = name == "turn/completed"
            ? parameters.TryGetProperty("turn", out var turn) ? Text(turn, "id") : null
            : Text(parameters, "turnId");
        return !string.IsNullOrWhiteSpace(turnId);
    }

    internal static string? Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
