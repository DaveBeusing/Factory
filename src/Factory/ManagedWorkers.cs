using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Factory;

/// <summary>Manages only worker processes launched by this Factory server instance.
/// External remote worker processes are never killed or otherwise assumed to be controllable.</summary>
public static class ManagedWorkers
{
    private static readonly ConcurrentDictionary<string, Process> Processes = new(StringComparer.Ordinal);
    public static IReadOnlyList<string> RunningIds()
    {
        foreach (var kv in Processes.ToArray())
        {
            try { if (kv.Value.HasExited) { Processes.TryRemove(kv.Key, out _); kv.Value.Dispose(); } }
            catch (InvalidOperationException) { Processes.TryRemove(kv.Key, out _); }
        }
        return Processes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }
    public static void Start(StateStore store, FactorySettings settings, string listenUrl, string id)
    {
        var worker = store.Read().Workers.Single(w => w.Id == id);
        if (!worker.Enabled || worker.Draining) throw new InvalidOperationException("Enable the worker before starting it");
        if (DateTimeOffset.UtcNow - worker.Heartbeat < TimeSpan.FromSeconds(20))
            throw new InvalidOperationException("Worker already connected; cannot start a second process using the same ID");
        if (RunningIds().Contains(id)) throw new InvalidOperationException("Worker process already running");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path not available");
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        var slug = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..20];
        psi.ArgumentList.Add("worker"); psi.ArgumentList.Add("--server"); psi.ArgumentList.Add(listenUrl);
        psi.ArgumentList.Add("--workspace"); psi.ArgumentList.Add(Path.Combine(store.Home, "managed-workers", slug));
        psi.ArgumentList.Add("--id"); psi.ArgumentList.Add(id);
        psi.Environment["FACTORY_TOKEN"] = settings.ApiToken;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to launch worker process");
        if (!Processes.TryAdd(id, process))
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw new InvalidOperationException("Worker already started");
        }
        store.Event(null, "worker_started", "Managed local process launched: " + id);
    }
    public static void Stop(StateStore store, string id)
    {
        if (store.Read().Jobs.Any(j => j.LeaseOwner == "remote:" + id &&
            j.LeaseUntil > DateTimeOffset.UtcNow && JobStates.Active.Contains(j.State)))
            throw new InvalidOperationException("Worker has an active job; drain it and stop after the job completes");
        if (!Processes.TryRemove(id, out var process))
            throw new InvalidOperationException("Worker is not managed by this server instance");
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
        finally { process.Dispose(); }
        store.Event(null, "worker_stopped", "Managed local process stopped: " + id);
    }
}
