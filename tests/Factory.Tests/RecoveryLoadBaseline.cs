using System.Diagnostics;
using Factory;

internal static class RecoveryLoadBaseline
{
    private const int BatchSize = 16;
    private const int RecoveryCallers = 4;
    private sealed record TurnFixture(Guid JobId, RemoteExecutorReceipt Receipt, string Digest);

    internal static async Task<object> MeasureAsync(DirectoryInfo root)
    {
        var controller = new StateStore(root.CreateSubdirectory("controller").FullName);
        var executor = new StateStore(root.CreateSubdirectory("executor").FullName);
        var receiptDirectory = Directory.CreateDirectory(Path.Combine(executor.Home, "executor-receipts"));
        var project = new ProjectInfo { Name = "Recovery load fixture" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 48 * 512 };
        var profile = new ProviderProfile { Id = "fixture", Kind = "ollama", Model = "fixture:1" };
        const string role = "review:code";
        var commit = new string('a', 40);
        var decision = new RemoteReceiptRecoveryInput(true, "Verified synthetic executor receipt", false);
        controller.Change(state => { state.Projects.Add(project); state.Goals.Add(goal); });
        var initial = Storage();
        var batches = new List<object>();

        for (var batch = 1; batch <= 3; batch++)
        {
            var fixtures = controller.Change(state => Enumerable.Range(0, BatchSize).Select(i =>
            {
                var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, Mode = JobExecutionModes.Remote,
                    Title = $"Recovery fixture {batch}/{i}" };
                state.Jobs.Add(job);
                JobRecovery.Assign(job, "remote:fixture", 300, local: false, state, reservationTokens: 512);
                job.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
                var lease = new RemoteProviderLease { JobId = job.Id, WorkerId = "fixture", ExecutionGeneration = job.ExecutionGeneration };
                state.RemoteProviderLeases.Add(lease);
                var usage = ProviderBudgetLedger.Begin(state, job, profile, role, profile.Model, 256, reserveUncapped: true);
                var turn = new RemoteProviderTurn { Id = Guid.NewGuid(), LeaseId = lease.Id, UsageId = usage.Id,
                    Role = role, InputCommit = commit, GrantDigest = new string('b', 64) };
                state.RemoteProviderTurns.Add(turn);
                return new TurnFixture(job.Id, new(turn.Id, lease.Id, profile.Id, role, commit, profile.Model,
                    30, 10, true, true, "VERDICT: PASS", false, "", [], 1000), turn.GrantDigest);
            }).ToArray());
            foreach (var fixture in fixtures)
                File.WriteAllText(Path.Combine(receiptDirectory.FullName, fixture.Receipt.TurnId.ToString("N") + ".receipt.json"),
                    Json.Stringify(new { grantDigest = fixture.Digest, receipt = fixture.Receipt }));

            var start = Stopwatch.GetTimestamp();
            var quarantined = JobRecovery.QuarantineExpired(controller, JobExecutionModes.Remote);
            var quarantineMs = Elapsed(start);
            var held = controller.Read();
            Require(quarantined.ToHashSet().SetEquals(fixtures.Select(f => f.JobId)) &&
                held.RemoteProviderTurns.Count(t => t.State == "held") == BatchSize &&
                held.ProviderBudgetReservations.Count(r => r.State == "held" && r.Consumed == 0) == BatchSize &&
                held.BudgetReservations.Count(r => r.State == GoalBudgetLedger.Held) == batch * BatchSize,
                "Quarantine did not preserve the expected holds");
            var heldStorage = Storage();

            var latencies = new double[BatchSize];
            start = Stopwatch.GetTimestamp();
            await Parallel.ForEachAsync(Enumerable.Range(0, BatchSize),
                new ParallelOptions { MaxDegreeOfParallelism = RecoveryCallers }, async (i, ct) =>
                {
                    var fixture = fixtures[i];
                    var begin = Stopwatch.GetTimestamp();
                    var receipt = await RemoteProviderExecutor.ReadReceiptAsync(executor,
                        new(fixture.Receipt.TurnId, fixture.Digest), ct)
                        ?? throw new InvalidOperationException("Durable fixture receipt was not found");
                    RemoteProviderRouting.SettleRecoveredReceipt(controller, fixture.JobId, receipt.TurnId, decision, receipt);
                    latencies[i] = Elapsed(begin);
                });
            var recoveryMs = Elapsed(start);

            foreach (var fixture in fixtures)
                RemoteProviderRouting.SettleRecoveredReceipt(controller, fixture.JobId, fixture.Receipt.TurnId, decision,
                    fixture.Receipt with { ReportedCostMicros = 9999 });

            var recovered = controller.Read();
            var total = batch * BatchSize;
            Require(recovered.RemoteProviderTurns.Count == total && recovered.RemoteProviderTurns.All(t => t.State == "reconciled") &&
                recovered.ProviderUsages.Count == total && recovered.ProviderUsages.All(u =>
                    u.InputTokens == 30 && u.OutputTokens == 10 && u.FinalUsageReported && u.ReportedCostMicros == 1000) &&
                recovered.ProviderBudgetReservations.All(r => r.State == "released" && r.Consumed == 40) &&
                recovered.BudgetReservations.All(r => r.State == GoalBudgetLedger.Held && r.Consumed == 40) &&
                recovered.Jobs.All(j => j.State == JobStates.Blocked && j.TokensIn == 30 && j.TokensOut == 10 && j.ExecutionGeneration == 2),
                "Recovery or replay changed settled usage, holds or fencing");
            batches.Add(new { batch, cumulativeTurns = total, quarantined = quarantined.Count, quarantineMs,
                recoveryMs, receiptRecoveryMs = QueueLoadBaseline.Percentiles(latencies), heldStorage, recoveredStorage = Storage() });
        }
        return new { batchSize = BatchSize, recoveryCallers = RecoveryCallers, initial, batches };

        object Storage()
        {
            var database = Path.Combine(controller.Home, "factory.sqlite3");
            return new { databaseBytes = Bytes(database), walBytes = Bytes(database + "-wal"),
                shmBytes = Bytes(database + "-shm"), receiptBytes = receiptDirectory.EnumerateFiles().Sum(file => file.Length) };
        }
    }

    private static long Bytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    private static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
