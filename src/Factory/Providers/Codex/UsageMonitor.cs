using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Factory;

public sealed class UsageWindow
{
    public double UsedPercent { get; set; }
    public double RemainingPercent { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public int WindowMinutes { get; set; }
}

public sealed class UsageSnapshot
{
    public UsageWindow? Weekly { get; set; }
    public UsageWindow? ShortWindow { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public bool Stale { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// Reads account-reported allowance snapshots; locally consumed tokens cannot establish
/// a remaining account allowance, particularly when other devices share the limit.
/// </summary>
public static class UsageMonitor
{
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, UsageSnapshot Value)> Cached = new();
    private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StaleLimit = TimeSpan.FromMinutes(15);

    private static JsonElement Property(JsonElement obj, params string[] keys)
    {
        if (obj.ValueKind != JsonValueKind.Object) return default;
        foreach (var key in keys)
            if (obj.TryGetProperty(key, out var val)) return val;
        return default;
    }
    private static double? Number(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out var d)) return d;
        if (node.ValueKind == JsonValueKind.String && double.TryParse(node.GetString(),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) return n;
        return null;
    }
    private static UsageWindow? Window(JsonElement node)
    {
        var percent = Number(Property(node, "usedPercent", "used_percent"));
        var minutes = Number(Property(node, "windowDurationMins", "windowMinutes", "window_minutes", "window_duration_mins"));
        if (!percent.HasValue || !minutes.HasValue || minutes.Value <= 0 || percent.Value < 0 || percent.Value > 100) return null;
        var resetNode = Property(node, "resetsAt", "resets_at");
        DateTimeOffset? reset = null;
        var epoch = Number(resetNode);
        if (epoch.HasValue && epoch.Value > 0)
        {
            try { reset = DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value); } catch (ArgumentOutOfRangeException) { }
        }
        return new UsageWindow
        {
            UsedPercent = percent.Value,
            RemainingPercent = 100 - percent.Value,
            ResetsAt = reset,
            WindowMinutes = (int)minutes.Value
        };
    }
    private static IEnumerable<UsageWindow> Windows(JsonElement node, int depth = 0)
    {
        if (depth > 7) yield break;
        var w = Window(node);
        if (w != null) yield return w;
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var prop in node.EnumerateObject())
                foreach (var child in Windows(prop.Value, depth + 1)) yield return child;
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray())
                foreach (var child in Windows(item, depth + 1)) yield return child;
    }

    /// <summary>Parse JSON-RPC result or bare rate-limit payload. No guessing from tokens.</summary>
    public static UsageSnapshot ParseResponse(string json, DateTimeOffset? observed = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = Property(doc.RootElement, "result");
        if (root.ValueKind == JsonValueKind.Undefined) root = doc.RootElement;
        var found = Windows(root).ToList();
        var weekly = found.FirstOrDefault(w => w.WindowMinutes == 7 * 24 * 60);
        var shortWindow = found.FirstOrDefault(w => w.WindowMinutes != 7 * 24 * 60 && w.WindowMinutes <= 24 * 60);
        return new UsageSnapshot
        {
            Weekly = weekly,
            ShortWindow = shortWindow,
            ObservedAt = observed ?? DateTimeOffset.UtcNow,
            Message = weekly == null ? "Weekly allowance not reported by the installed agent CLI." : ""
        };
    }

    public static Task<UsageSnapshot> GetAsync(string home, CancellationToken cancellationToken = default) =>
        GetAsync(home, ReadLiveAsync, cancellationToken);

    internal static async Task<UsageSnapshot> GetAsync(string home,
        Func<CancellationToken, Task<UsageSnapshot>> readLive, CancellationToken cancellationToken = default)
    {
        home = Path.GetFullPath(home);
        var now = DateTimeOffset.UtcNow;
        if (Cached.TryGetValue(home, out var cached) && now - cached.At < CacheAge) return cached.Value;
        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (Cached.TryGetValue(home, out cached) && now - cached.At < CacheAge) return cached.Value;
            try
            {
                var latest = await readLive(cancellationToken);
                if (latest.Weekly == null && latest.ShortWindow == null)
                    throw new InvalidDataException("No recognized account allowance windows were returned");
                Cached[home] = (now, latest);
                Directory.CreateDirectory(home);
                var save = Path.Combine(home, "usage.json");
                File.WriteAllText(save + ".tmp", Json.Stringify(latest));
                File.Move(save + ".tmp", save, true);
                return latest;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                UsageSnapshot? previous = null;
                if (Cached.TryGetValue(home, out var value)) previous = value.Value;
                if (previous == null)
                {
                    try
                    {
                        var path = Path.Combine(home, "usage.json");
                        if (File.Exists(path)) previous = JsonSerializer.Deserialize<UsageSnapshot>(File.ReadAllText(path), Json.Options);
                    }
                    catch (Exception) { /* Corrupt snapshots are not trusted. */ }
                }
                var message = "Live account usage unavailable: " + ex.Message;
                if (previous?.ObservedAt is { } at && now - at <= StaleLimit)
                {
                    return new UsageSnapshot { Weekly = previous.Weekly, ShortWindow = previous.ShortWindow,
                        ObservedAt = at, Stale = true, Message = message };
                }
                return new UsageSnapshot { Stale = true, Message = message };
            }
        }
        finally { RefreshLock.Release(); }
    }

    private static async Task<UsageSnapshot> ReadLiveAsync(CancellationToken cancellationToken)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        scope.CancelAfter(TimeSpan.FromSeconds(12));
        using var process = new Process { StartInfo = Commands.StartInfo("codex", ["app-server"], Environment.CurrentDirectory) };
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(scope.Token);
        try
        {
            async Task Send(object obj)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(obj));
                await process.StandardInput.FlushAsync(scope.Token);
            }
            async Task<JsonElement> ReadResponse(int id)
            {
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(scope.Token)
                        ?? throw new IOException("Agent app-server closed before the usage response");
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("method", out var method) && root.TryGetProperty("id", out var requestId))
                    {
                        // Never approve external requests while reading an account allowance.
                        await Send(new { id = requestId.Clone(), result = new { decision = "decline" } });
                        continue;
                    }
                    if (!root.TryGetProperty("id", out var replyId) || !replyId.TryGetInt32(out var got) || got != id) continue;
                    if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
                    return root.Clone();
                }
            }
            await Send(new { method = "initialize", id = 1, @params = new { clientInfo = new
                { name = "factory", title = "Factory", version = "1.0" } } });
            await ReadResponse(1);
            await Send(new { method = "initialized", @params = new { } });
            await Send(new { method = "account/rateLimits/read", id = 2, @params = new { } });
            return ParseResponse((await ReadResponse(2)).GetRawText());
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch { } }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            try { await stderr; } catch { }
        }
    }
}
