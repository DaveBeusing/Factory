using System.Diagnostics;
using System.Runtime.InteropServices;
using Factory;
using Microsoft.Data.Sqlite;

internal static class QueueLoadBaseline
{
    private const int Jobs = 64;
    private const int Repetitions = 3;
    private const int WriterHoldMs = 250;

    public static async Task RunAsync()
    {
        var scratch = Directory.CreateTempSubdirectory("factory-queue-load-");
        try
        {
            await MeasureClaimsAsync(scratch.CreateSubdirectory("warmup").FullName, 8, 2);
            var rounds = new List<object>();
            for (var round = 1; round <= Repetitions; round++)
            {
                foreach (var workers in round % 2 == 1 ? new[] { 1, 8 } : new[] { 8, 1 })
                {
                    var result = await MeasureClaimsAsync(scratch.CreateSubdirectory($"round-{round}-{workers}").FullName,
                        Jobs, workers);
                    rounds.Add(new { round, workers, result });
                }
            }

            var sqlite = await MeasureWriterAsync(scratch.CreateSubdirectory("writer").FullName);
            var recovery = await RecoveryLoadBaseline.MeasureAsync(scratch.CreateSubdirectory("recovery"));
            Console.WriteLine(Json.Stringify(new
            {
                environment = new { os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    runtime = RuntimeInformation.FrameworkDescription, logicalProcessors = Environment.ProcessorCount },
                jobsPerRound = Jobs, rounds, sqlite, recovery
            }));
        }
        finally { scratch.Delete(recursive: true); }
    }

    private static async Task<object> MeasureClaimsAsync(string home, int jobCount, int workers)
    {
        var store = new StateStore(home);
        var workerIds = Enumerable.Range(0, workers).Select(i => $"fixture-{i}").ToArray();
        var now = DateTimeOffset.UtcNow;
        var project = new ProjectInfo { Name = "Queue fixture", Origin = "https://example.invalid/fixture", MaxParallel = jobCount };
        var settings = new FactorySettings { AllowPush = true, Runtime = "exec", MaxParallel = jobCount, LeaseSeconds = 300 };
        store.Change(state =>
        {
            state.Projects.Add(project);
            foreach (var id in workerIds)
            {
                state.Workers.Add(new WorkerInfo { Id = id });
                state.WorkerProjectGrants.Add(new() { WorkerId = id, ProjectId = project.Id });
                foreach (var capability in new[] { "native-agent", "git-cli", "runtime:exec" })
                    state.WorkerCapabilityProofs.Add(new()
                    { WorkerId = id, Capability = capability, Evidence = "Synthetic fixture", VerifiedAt = now });
            }
            for (var i = 0; i < jobCount; i++)
                state.Jobs.Add(new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Remote,
                    Title = $"Fixture job {i}", ExpectedPaths = [$"fixture/{i}.txt"], CreatedAt = now.AddTicks(i) });
        });

        var start = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = workerIds.Select(async worker =>
        {
            var released = await start.Task;
            var samples = new List<(Guid JobId, double ClaimMs, double QueueMs)>();
            for (var i = 0; i < jobCount / workers; i++)
            {
                var begin = Stopwatch.GetTimestamp();
                var claim = WorkerProtocol.Claim(store, settings, worker)
                    ?? throw new InvalidOperationException($"{worker} received no claim for a seeded job");
                var end = Stopwatch.GetTimestamp();
                samples.Add((claim.Job.Id, Stopwatch.GetElapsedTime(begin, end).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(released, end).TotalMilliseconds));
            }
            return samples;
        }).ToArray();
        start.SetResult(Stopwatch.GetTimestamp());
        var measured = (await Task.WhenAll(callers)).SelectMany(samples => samples).ToArray();
        var persisted = store.Read();
        var claimedIds = measured.Select(sample => sample.JobId).ToHashSet();
        var claimEvents = persisted.Events.Where(e => e.Type == "remote_claim").ToArray();
        if (measured.Length != jobCount || claimedIds.Count != jobCount ||
            !claimedIds.SetEquals(persisted.Jobs.Where(j => JobStates.Active.Contains(j.State) && j.ExecutionGeneration == 1)
                .Select(j => j.Id)) || claimEvents.Length != jobCount ||
            !claimedIds.SetEquals(claimEvents.Select(e => e.JobId ?? Guid.Empty)))
            throw new InvalidOperationException("Claims did not persist exactly one lease and event for each job");

        var drainMs = measured.Max(sample => sample.QueueMs);
        return new { claimed = measured.Length, drainMs = Math.Round(drainMs, 2),
            claimsPerSecond = Math.Round(jobCount * 1000 / drainMs, 2),
            claimMs = Percentiles(measured.Select(sample => sample.ClaimMs)),
            queueToClaimMs = Percentiles(measured.Select(sample => sample.QueueMs)) };
    }

    private static async Task<object> MeasureWriterAsync(string home)
    {
        var store = new StateStore(home);
        using var holder = Connect();
        using var contender = Connect();
        using var versionCommand = holder.CreateCommand();
        versionCommand.CommandText = "SELECT sqlite_version()";
        var sqliteVersion = (string)versionCommand.ExecuteScalar()!;
        var idle = Enumerable.Range(0, 20).Select(_ => Acquire(contender)).ToArray();
        var held = new List<double>();
        var contended = new List<double>();
        for (var i = 0; i < Repetitions; i++)
        {
            using var transaction = holder.BeginTransaction(deferred: false);
            var starting = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acquisition = Task.Run(() => Acquire(contender, starting));
            var begin = await starting.Task;
            await Task.Delay(WriterHoldMs);
            transaction.Rollback();
            held.Add(Math.Round(Stopwatch.GetElapsedTime(begin).TotalMilliseconds, 2));
            contended.Add(Math.Round(await acquisition, 2));
        }
        return new { sqliteVersion, writerHoldTargetMs = WriterHoldMs, idleSamples = idle.Length,
            idleAcquireMs = Percentiles(idle), writerReleaseMs = held, contendedAcquireMs = contended };

        SqliteConnection Connect()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(store.Home, "factory.sqlite3"), Pooling = false, DefaultTimeout = 30 }.ToString());
            connection.Open();
            return connection;
        }

        static double Acquire(SqliteConnection connection, TaskCompletionSource<long>? starting = null)
        {
            var begin = Stopwatch.GetTimestamp();
            starting?.SetResult(begin);
            using var transaction = connection.BeginTransaction(deferred: false);
            return Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        }
    }

    internal static object Percentiles(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        double At(double fraction) => Math.Round(sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1], 2);
        return new { p50 = At(0.50), p95 = At(0.95), max = At(1) };
    }
}
