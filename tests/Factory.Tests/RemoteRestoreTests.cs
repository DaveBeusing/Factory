using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Factory;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

internal static class RemoteRestoreTests
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var scratch = Directory.CreateTempSubdirectory("factory-remote-restore-");
        try { await VerifyAsync(scratch.FullName, ct); }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (OperatingSystem.IsWindows())
                foreach (var file in scratch.EnumerateFiles("*", SearchOption.AllDirectories))
                    if (file.IsReadOnly) file.IsReadOnly = false;
            scratch.Delete(recursive: true);
        }
    }

    private static async Task VerifyAsync(string root, CancellationToken ct)
    {
        const string model = "fixture:1";
        var calls = 0;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        await using var provider = builder.Build();
        provider.Urls.Add("http://127.0.0.1:0");
        provider.MapPost("/api/generate", () =>
        {
            Interlocked.Increment(ref calls);
            return new { model, done = true, response = "VERDICT: PASS", prompt_eval_count = 30, eval_count = 10 };
        });
        await provider.StartAsync(ct);
        var endpoint = provider.Urls.Single();
        var profile = new ProviderProfile { Id = "restore-review", Kind = "ollama", Model = model, Endpoint = endpoint };
        var executorHome = Path.Combine(root, "executor");
        var controllerHome = Path.Combine(root, "controller");
        var executorSettings = new FactorySettings { EnableRemoteProviderExecutor = true, Providers = [profile] };
        WriteSettings(executorHome, executorSettings);
        var oldToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var newToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var (baseSha, head, tree, bundle) = await MakeBundleAsync(root, ct);
        var project = new ProjectInfo { Name = "Restore fixture", Path = root };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 1024 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, Mode = JobExecutionModes.Remote, Title = "Review fixture" };
        var lease = new RemoteProviderLease { JobId = job.Id, WorkerId = "restore-fixture" };
        var route = new RemoteProviderRoute("review:code", profile.Id, profile.Kind, "", model, endpoint, 0, 0);
        var turnId = Guid.NewGuid();
        var controllerSettings = new FactorySettings { EnableRemoteProviderRouting = true, Providers = [profile] };
        var controllerStore = new StateStore(controllerHome);
        RemoteExecutorGrant grant;
        using (var executor = await DashboardProcess.StartAsync(executorHome, oldToken, "http://127.0.0.1:0", ct))
        {
            lease.ExecutorUrl = controllerSettings.RemoteProviderExecutorUrl = executor.Address;
            WriteSettings(controllerHome, controllerSettings);
            grant = controllerStore.Change(state =>
            {
                state.Paused = true;
                state.Projects.Add(project);
                state.Goals.Add(goal);
                state.Jobs.Add(job);
                JobRecovery.Assign(job, "remote:" + lease.WorkerId, 120, local: false, state, reservationTokens: 512);
                lease.ExecutionGeneration = job.ExecutionGeneration;
                state.RemoteProviderLeases.Add(lease);
                var usage = ProviderBudgetLedger.Begin(state, job, profile, route.Role, model, 256, reserveUncapped: true);
                var issued = new RemoteExecutorGrant(turnId, lease.Id, job.Id, job.ExecutionGeneration, route,
                    head, baseSha, tree, "example/restore-fixture", false, 256, "Review fixture", bundle, ["fixture.txt"], []);
                state.RemoteProviderTurns.Add(new RemoteProviderTurn { Id = turnId, LeaseId = lease.Id, UsageId = usage.Id,
                    Role = route.Role, InputCommit = head, GrantDigest = RemoteProviderExecutor.Digest(issued) });
                return issued;
            });
            using var controller = await DashboardProcess.StartAsync(controllerHome, oldToken, "http://127.0.0.1:0", ct);
            using var executorClient = Client(executor.Address, oldToken);
            using var response = await executorClient.PostAsJsonAsync("api/executor/turn", grant, Json.Options, ct);
            await RequireStatus(response, HttpStatusCode.OK, "initial executor dispatch", ct);
            // Simulate losing the response before the controller records it.
            Check(calls == 1 && controllerStore.Job(job.Id).TokensIn == 0, "Unexpected pre-backup provider or ledger state");
            controllerStore.Change(state => state.Jobs.Single().LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1));
        }

        SqliteConnection.ClearAllPools();
        var controllerBackup = Path.Combine(root, "controller-backup");
        var executorBackup = Path.Combine(root, "executor-backup");
        var restoredControllerHome = Path.Combine(root, "restored-controller");
        var restoredExecutorHome = Path.Combine(root, "restored-executor");
        CopyState(controllerHome, controllerBackup);
        CopyState(executorHome, executorBackup);
        CopyState(controllerBackup, restoredControllerHome);
        CopyState(executorBackup, restoredExecutorHome);

        using var restoredExecutor = await DashboardProcess.StartAsync(restoredExecutorHome, newToken, lease.ExecutorUrl, ct);
        using var restoredController = await DashboardProcess.StartAsync(restoredControllerHome, newToken, "http://127.0.0.1:0", ct);
        using var oldClient = Client(restoredExecutor.Address, oldToken);
        using var executorReader = Client(restoredExecutor.Address, newToken);
        using var admin = Client(restoredController.Address, controllerSettings.ApiToken);
        var query = new RemoteProviderExecutor.ReceiptQuery(turnId, RemoteProviderExecutor.Digest(grant));
        await PostAsync(oldClient, "api/executor/receipt", query, HttpStatusCode.Unauthorized, ct);
        await PostAsync(oldClient, "api/executor/turn", grant, HttpStatusCode.Unauthorized, ct);
        await PostAsync(executorReader, "api/executor/receipt", query, HttpStatusCode.OK, ct);
        await PostAsync(executorReader, "api/executor/turn", grant, HttpStatusCode.InternalServerError, ct);
        Check(calls == 1, "Restored executor invoked the provider on replay");

        var restoredStore = new StateStore(restoredControllerHome);
        await PostAsync(admin, "api/queue/recover", new { }, HttpStatusCode.OK, ct);
        var held = restoredStore.Read();
        Check(held.Paused && held.RemoteProviderTurns.Single().State == "held" &&
            held.ProviderBudgetReservations.Single().State == "held" && held.BudgetReservations.Single().State == GoalBudgetLedger.Held,
            "Restored expired lease did not retain both budget holds");
        var recover = $"api/jobs/{job.Id}/provider-turns/{turnId}/recover";
        var decision = new RemoteReceiptRecoveryInput(true, "Verified restored executor receipt", false);
        var receiptName = turnId.ToString("N") + ".receipt.json";
        var restoredReceipt = Path.Combine(restoredExecutorHome, "executor-receipts", receiptName);
        File.Delete(restoredReceipt);
        await PostAsync(admin, recover, decision, HttpStatusCode.BadRequest, ct);
        Check(restoredStore.Read().ProviderBudgetReservations.Single() is { State: "held", Consumed: 0 } &&
            restoredStore.Read().BudgetReservations.Single() is { State: GoalBudgetLedger.Held, Consumed: 0 },
            "A missing receipt released or debited held usage");
        await PostAsync(executorReader, "api/executor/turn", grant, HttpStatusCode.InternalServerError, ct);
        Check(calls == 1, "Removing the restored receipt released the dispatch marker");
        File.Copy(Path.Combine(executorBackup, "executor-receipts", receiptName), restoredReceipt);
        await PostAsync(admin, recover, decision, HttpStatusCode.OK, ct);
        await PostAsync(admin, recover, decision, HttpStatusCode.OK, ct);
        var settled = restoredStore.Read();
        Check(settled.RemoteProviderTurns.Single() is { State: "reconciled", ObservedModel: model } &&
            settled.ProviderBudgetReservations.Single() is { State: "released", Consumed: 40 } &&
            settled.ProviderUsages.Single() is { InputTokens: 30, OutputTokens: 10, FinalUsageReported: true } &&
            settled.BudgetReservations.Single() is { State: GoalBudgetLedger.Held, Consumed: 40 } &&
            settled.Jobs.Single() is { State: JobStates.Blocked, TokensIn: 30, TokensOut: 10 } && calls == 1,
            "Recovery did not charge exactly once or reopened the fenced job");
    }

    private static async Task<(string Base, string Head, string Tree, byte[] Bundle)> MakeBundleAsync(string root, CancellationToken ct)
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "repository")).FullName;
        Task<string> Git(params string[] args) => RemoteProviderExecutor.GitAsync(repo, ct, args);
        Task<string> Commit(string message) => Git("-c", "user.name=Factory Fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "-m", message);
        await Git("init", "-b", "master");
        var file = Path.Combine(repo, "fixture.txt");
        await File.WriteAllTextAsync(file, "before\n", ct);
        await Git("add", "fixture.txt");
        await Commit("Baseline");
        var baseline = await Git("rev-parse", "HEAD");
        await File.WriteAllTextAsync(file, "after\n", ct);
        await Git("add", "fixture.txt");
        await Commit("Review input");
        var bundle = Path.Combine(root, "review.bundle");
        await Git("bundle", "create", bundle, "HEAD");
        return (baseline, await Git("rev-parse", "HEAD"), await Git("write-tree"), await File.ReadAllBytesAsync(bundle, ct));
    }

    private static void WriteSettings(string directory, FactorySettings settings)
    {
        Directory.CreateDirectory(directory);
        settings.EnsureCredentials();
        File.WriteAllText(Path.Combine(directory, "settings.json"), Json.Stringify(settings));
    }

    private static void CopyState(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyState(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static HttpClient Client(string address, string token) => new(new HttpClientHandler { UseProxy = false })
    {
        BaseAddress = new Uri(address), DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
    };

    private static async Task PostAsync<T>(HttpClient client, string path, T body, HttpStatusCode expected, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, body, Json.Options, ct);
        await RequireStatus(response, expected, path, ct);
    }

    private static async Task RequireStatus(HttpResponseMessage response, HttpStatusCode expected, string operation, CancellationToken ct)
    {
        if (response.StatusCode != expected)
            throw new InvalidOperationException($"{operation} returned {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed class DashboardProcess(Process process, string address) : IDisposable
    {
        public string Address { get; } = address;

        public static async Task<DashboardProcess> StartAsync(string home, string token, string url, CancellationToken ct)
        {
            var start = Commands.StartInfo("dotnet", [typeof(DashboardHost).Assembly.Location, "dashboard", "--listen", url], home);
            start.Environment["FACTORY_HOME"] = home;
            start.Environment["FACTORY_REMOTE_EXECUTOR_TOKEN"] = token;
            start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";
            var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Factory dashboard");
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try
            {
                const string ready = "Now listening on: ";
                while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    var marker = line.IndexOf(ready, StringComparison.Ordinal);
                    if (marker < 0) continue;
                    _ = process.StandardOutput.ReadToEndAsync(ct);
                    return new DashboardProcess(process, line[(marker + ready.Length)..].Trim());
                }
                throw new InvalidOperationException("Dashboard exited before listening: " + await stderr);
            }
            catch
            {
                Stop(process);
                throw;
            }
        }

        public void Dispose() => Stop(process);

        private static void Stop(Process process)
        {
            using (process)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(10_000)) throw new TimeoutException("Factory dashboard did not stop");
            }
        }
    }
}
