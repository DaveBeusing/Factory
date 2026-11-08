using System.Diagnostics;
using Factory;

if (args is ["--verify-windows-package", var windowsPackage])
{
    await WindowsPackageValidation.RunAsync(windowsPackage);
    return 0;
}

if (args is ["--verify-remote-provider-leases"])
{
    await RemoteProviderLeaseTests.RunAsync();
    await RemoteProviderLeaseTests.VerifyMovingPullHeadAsync();
    Console.WriteLine("PASS remote provider leases and executor");
    return 0;
}

if (args is ["--verify-remote-restore"])
{
    await RemoteRestoreTests.RunAsync();
    Console.WriteLine("PASS separate controller and executor restore, replay fencing and credential rotation");
    return 0;
}

if (args is ["--browser-recovery-fixture", var browserFixtureHome])
{
    BrowserRecoveryFixture.Seed(browserFixtureHome);
    return 0;
}

if (args is ["--browser-provider-evidence-fixture", var providerEvidenceFixtureHome, var fixtureExecutorUrl])
{
    BrowserRecoveryFixture.SeedProviderEvidence(providerEvidenceFixtureHome, fixtureExecutorUrl);
    return 0;
}

if (args is ["--browser-project-recovery-fixture", var projectRecoveryHome, var projectRecoveryExecutorUrl])
{
    Console.WriteLine(Json.Stringify(BrowserRecoveryFixture.SeedProjectRecovery(projectRecoveryHome, projectRecoveryExecutorUrl)));
    return 0;
}

if (args is ["--browser-project-jobs-fixture", var projectJobsHome, var projectJobsWorktree, var projectJobsCheckpoint])
{
    Console.WriteLine(Json.Stringify(BrowserRecoveryFixture.SeedProjectJobs(projectJobsHome, projectJobsWorktree, projectJobsCheckpoint)));
    return 0;
}

if (args is ["--browser-project-activity-fixture", var activityHome])
{
    Console.WriteLine(Json.Stringify(BrowserRecoveryFixture.SeedProjectActivity(activityHome)));
    return 0;
}

if (args is ["--browser-append-project-activity", var activityEventHome, var activityJobId])
{
    new StateStore(activityEventHome).Event(Guid.Parse(activityJobId), "job_control", "private live event details");
    return 0;
}

if (args is ["--browser-project-planning-fixture", var planningHome, var planningRepository])
{
    Console.WriteLine(Json.Stringify(BrowserRecoveryFixture.SeedProjectPlanning(planningHome, planningRepository)));
    return 0;
}

if (args is ["--browser-advance-job-checkpoint", var checkpointHome, var checkpointJob, var nextCheckpoint])
{
    new StateStore(checkpointHome).UpdateJob(Guid.Parse(checkpointJob), job =>
    {
        job.ExecutionGeneration++;
        JobRecovery.Checkpoint(job, "agent", nextCheckpoint);
    });
    return 0;
}

if (args is ["--provider-crash-fixture", var fixtureHome, var projectArg, var goalArg])
{
    var store = new StateStore(fixtureHome);
    var runtime = new ProjectScopedAgentRuntime(new ProviderCrashFixture(), store,
        Guid.Parse(projectArg), Guid.Parse(goalArg), new ProviderProfile(), "planner", "fixture", 512);
    await runtime.RunAsync(fixtureHome, "fixture", "", CancellationToken.None, readOnly: true);
    return 1;
}

if (args.FirstOrDefault() == "--safe-mode")
{
    var expected = new[] { "--safe-mode", "--restricted", "--print", "--output-format", "json", "--no-session-persistence",
        "--permission-mode", "plan", "--tools", "Read,Glob,Grep", "--disable-slash-commands",
        "--strict-mcp-config", "--mcp-config", "{}", "--disallowed-tools", "mcp__*", "--no-chrome",
        "--model", "sonnet" };
    if (!args.SequenceEqual(expected)) return 23;
    var prompt = await Console.In.ReadToEndAsync();
    switch (prompt)
    {
        case "complete":
            Console.WriteLine("""{"type":"result","subtype":"success","is_error":false,"result":"fixture","usage":{"input_tokens":8,"output_tokens":4,"cache_read_input_tokens":2,"cache_creation_input_tokens":3},"total_cost_usd":0.012345}""");
            return 0;
        case "cost-only failure":
            Console.WriteLine("""{"type":"result","subtype":"error_during_execution","is_error":true,"total_cost_usd":0.000004}""");
            return 1;
        case "missing cost":
            Console.WriteLine("""{"type":"result","subtype":"success","is_error":false,"result":"fixture","usage":{"input_tokens":1,"output_tokens":1}}""");
            return 0;
        default:
            return 24;
    }
}

if (Environment.GetEnvironmentVariable("FACTORY_PREFLIGHT_GH_HEAD") is { } fixtureHead &&
    args is [var kind, "list", ..])
{
    var repository = Array.IndexOf(args, "--repo");
    if (repository < 0 || args.ElementAtOrDefault(repository + 1) != "github.com/fixture/registered")
        return 24;
    if (kind == "issue")
    {
        Console.WriteLine("[]");
        return 0;
    }
    if (kind != "pr" || !args.Contains("number,title,headRefName,headRefOid,url")) return 25;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new[]
    {
        new { number = 17, title = "Diagnostics changes", headRefName = "feature/diagnostics",
            headRefOid = fixtureHead, url = "https://github.com/fixture/registered/pull/17" }
    }));
    return 0;
}

var results = new List<(string Name, bool Passed, string Error)>();
void Test(string name, Action test)
{
    try { test(); results.Add((name, true, "")); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { results.Add((name, false, ex.Message)); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new Exception(message);
}

// Provisioning is an administrator operation, never part of worker self-registration.
void GrantRemoteWorker(StateStore store, string workerId, params Guid[] projectIds)
{
    MissionControlManager.SetWorkerPermissions(store, workerId, new WorkerPermissionsInput
    {
        ProjectIds = projectIds.ToList(),
        Reason = "Fixture operator verified worker capabilities",
        VerifiedCapabilities =
        [
            new() { Capability = "native-agent", Evidence = "Reviewed native agent execution in test fixture" },
            new() { Capability = "git-cli", Evidence = "Reviewed Git CLI installation in test fixture" },
            new() { Capability = "runtime:exec", Evidence = "Reviewed exec adapter in test fixture" },
            new() { Capability = "runtime:app-server", Evidence = "Reviewed app server adapter in test fixture" },
            new() { Capability = "native-checks", Evidence = "Reviewed native checks in test fixture" },
            new() { Capability = "container-checks", Evidence = "Reviewed container checks in test fixture" }
        ]
    });
}

Test("Provider routing is project-scoped and rejects unsupported builder capabilities", () =>
{
    var project = new ProjectInfo { Name = "multi-provider" };
    var state = new FactoryState();
    state.Projects.Add(project);
    var settings = new FactorySettings
    {
        Providers =
        [
            new ProviderProfile { Id = "codex", Kind = "codex" },
            new ProviderProfile { Id = "local-model", Kind = "ollama",
                Model = "review-model", Endpoint = "http://127.0.0.1:11434" }
        ]
    };
    state.ProviderAssignments.Add(new ProviderAssignment
        { ProjectId = project.Id, Role = "review", ProviderId = "local-model" });
    Assert(ProviderRouter.Select(settings, state, project.Id, "review:security", "high").Id == "local-model");
    Assert(ProviderRouter.Select(settings, state, project.Id, "builder", "high").Id == "codex");
    state.ProviderAssignments.Add(new ProviderAssignment
        { ProjectId = project.Id, Role = "builder", ProviderId = "local-model" });
    try
    {
        ProviderRouter.Select(settings, state, project.Id, "builder", "high");
        throw new Exception("Text-only provider was allowed to modify source");
    }
    catch (InvalidOperationException) { }
    try
    {
        ProviderRouter.Validate(new ProviderProfile
            { Id = "remote", Kind = "ollama", Model = "unsafe", Endpoint = "http://example.com:11434" },
            "planner");
        throw new Exception("Non-loopback Ollama endpoint was allowed");
    }
    catch (InvalidOperationException) { }
});

Test("Provider ledger preserves its public three-parameter CLR signature", () =>
{
    var method = typeof(ProviderBudgetLedger).GetMethod(nameof(ProviderBudgetLedger.Record),
        [typeof(FactoryState), typeof(Guid), typeof(AgentTokenUsage)]);
    Assert(method is { IsPublic: true, IsStatic: true } && method.ReturnType == typeof(long),
        "Precompiled callers require the original Record signature");
});

Test("Provider budget reservations guard concurrent capacity and preserve unknown usage", () =>
{
    var p = new ProjectInfo { Name = "budgeted" };
    var j = new FactoryJob { ProjectId = p.Id };
    var state = new FactoryState();
    state.Projects.Add(p);
    state.Jobs.Add(j);
    var provider = new ProviderProfile { Id = "limited", Kind = "codex", TokenLimit = 1000 };
    state.ProviderBudgetLimits.Add(new ProviderBudgetLimit
        { ProjectId = p.Id, ProviderId = provider.Id, TokenLimit = 800 });
    var first = ProviderBudgetLedger.Begin(state, j, provider, "builder", "", 600);
    Assert(state.ProviderBudgetReservations.Single().Reserved == 600);
    try
    {
        ProviderBudgetLedger.Begin(state, j, provider, "review:code", "", 600);
        throw new Exception("Reserved provider capacity was oversubscribed");
    }
    catch (TokenBudgetExceededException) { }
    Assert(ProviderBudgetLedger.Record(state, first.Id, new AgentTokenUsage(150, 100)) == 350);
    ProviderBudgetLedger.Finish(state, first.Id, false);
    Assert(state.ProviderBudgetReservations.Single().State == "held");
    try
    {
        ProviderBudgetLedger.Begin(state, j, provider, "review:code", "", 300);
        throw new Exception("Held budget liability was released without reconciliation");
    }
    catch (TokenBudgetExceededException) { }
    ProviderBudgetLedger.Finish(state, first.Id, true);
    Assert(state.ProviderBudgetReservations.Single().State == "held" && !first.FinalUsageReported,
        "A late runner response released a held reservation without operator reconciliation");
});

Test("Provider usage, assignments and local budgets persist in SQLite", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-provider-schema-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var p = new ProjectInfo { Name = "persistence" };
        var j = new FactoryJob { ProjectId = p.Id };
        store.Change(s =>
        {
            s.Projects.Add(p); s.Jobs.Add(j);
            s.ProviderAssignments.Add(new ProviderAssignment
                { ProjectId = p.Id, Role = "planner", ProviderId = "codex" });
            s.ProviderBudgetLimits.Add(new ProviderBudgetLimit
                { ProjectId = p.Id, ProviderId = "codex", TokenLimit = 20000 });
            var usage = ProviderBudgetLedger.Begin(s, j,
                new ProviderProfile { Id = "codex", TokenLimit = 20000 }, "builder", "", 1024);
            ProviderBudgetLedger.Record(s, usage.Id, new AgentTokenUsage(120, 30));
            ProviderBudgetLedger.Finish(s, usage.Id, true);
        });
        var state = new StateStore(home).Read();
        Assert(state.ProviderAssignments.Single().Role == "planner");
        Assert(state.ProviderBudgetLimits.Single().TokenLimit == 20000);
        Assert(state.ProviderUsages.Single().InputTokens == 120);
        Assert(state.ProviderBudgetReservations.Single().State == "released");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Provider-scoped planning is charged to the goal and attributed model", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-provider-planning-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "planning" };
        var goal = new FactoryGoal { ProjectId = project.Id, TokenBudget = 5000 };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); });
        var profile = new ProviderProfile { Id = "planner-model", Kind = "codex", TokenLimit = 1000 };
        var tracked = new ProjectScopedAgentRuntime(new FakeAgent(), store,
            project.Id, goal.Id, profile, "planner", "planning-v1", 512);
        var result = tracked.RunAsync(home, "Review this bounded context", "", CancellationToken.None,
            readOnly: true).GetAwaiter().GetResult();
        Assert(result.Success && result.UsageReported);
        var state = store.Read();
        var entry = state.ProviderUsages.Single();
        Assert(entry.JobId is null && entry.Role == "planner" && entry.Model == "planning-v1");
        Assert(entry.InputTokens == 8 && entry.OutputTokens == 4 && entry.FinalUsageReported);
        Assert(state.ProviderBudgetReservations.Single().State == "released");
        using var summary = System.Text.Json.JsonDocument.Parse(Json.Stringify(GoalBudgetLedger.Summary(state, goal.Id)));
        var provider = summary.RootElement.GetProperty("providers")[0];
        Assert(provider.GetProperty("providerId").GetString() == profile.Id);
        Assert(provider.GetProperty("inputTokens").GetInt64() == 8 &&
            provider.GetProperty("outputTokens").GetInt64() == 4);
        Assert(GoalBudgetLedger.Available(state, goal.Id) == 4988,
            "Provider planning consumption was omitted from goal liability");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Controller crash holds provider reservations and permits verified recovery", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-provider-crash-" + Guid.NewGuid().ToString("N"));
    Process? controller = null;
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "crash fixture" };
        var goal = new FactoryGoal { ProjectId = project.Id, TokenBudget = 2000 };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); });
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        foreach (var arg in new[] { "--provider-crash-fixture", home, project.Id.ToString(), goal.Id.ToString() })
            start.ArgumentList.Add(arg);
        controller = Process.Start(start) ?? throw new Exception("Cannot start fixture controller");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var marker = Path.Combine(home, "provider-dispatched");
        while (!File.Exists(marker) && !controller.HasExited)
            await Task.Delay(50, timeout.Token);
        Assert(File.Exists(marker) && !controller.HasExited, "Provider turn never started");

        JobRecovery.QuarantineExpired(store);
        var reservation = store.Read().ProviderBudgetReservations.Single();
        Assert(reservation.State == "active" && reservation.OwnerProcessId == controller.Id,
            "Recovery changed a live controller's reservation");
        controller.Kill();
        await controller.WaitForExitAsync(timeout.Token);

        var reopened = new StateStore(home);
        JobRecovery.QuarantineExpired(reopened);
        JobRecovery.QuarantineExpired(reopened);
        var recovered = reopened.Read();
        Assert(recovered.ProviderBudgetReservations.Single().State == "held");
        Assert(recovered.Events.Count(e => e.Type == "provider_budget_held") == 1);
        Assert(GoalBudgetLedger.Available(recovered, goal.Id) == 1488, "Unknown usage was released");
        ProviderBudgetReconciliation.Reconcile(reopened, reservation.Id, new()
        {
            Reviewed = true, Reason = "Fixture verified final use", FinalInputTokens = 15, FinalOutputTokens = 5
        });
        var runtime = new ProjectScopedAgentRuntime(new FakeAgent(), reopened, project.Id, goal.Id,
            new ProviderProfile(), "planner", "fixture", 512);
        Assert((await runtime.RunAsync(home, "retry", "", timeout.Token, readOnly: true)).Success);
        Assert(GoalBudgetLedger.Available(reopened.Read(), goal.Id) == 1968);
    }
    finally
    {
        if (controller is not null)
        {
            if (!controller.HasExited) { controller.Kill(); await controller.WaitForExitAsync(); }
            controller.Dispose();
        }
        try { Directory.Delete(home, true); } catch { }
    }
});

Test("Schema 7 provider reservations upgrade to held without releasing capacity", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-provider-upgrade-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "schema 7 fixture" };
        var goal = new FactoryGoal { ProjectId = project.Id, TokenBudget = 2000 };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Goals.Add(goal);
            ProviderBudgetLedger.Begin(s, new FactoryJob { Id = Guid.Empty, ProjectId = project.Id, GoalId = goal.Id },
                new ProviderProfile(), "planner", "fixture", 512);
        });
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var downgrade = db.CreateCommand();
            downgrade.CommandText = """
                ALTER TABLE provider_usages DROP COLUMN reported_cost_micros;
                ALTER TABLE provider_budget_reservations DROP COLUMN owner_process_id;
                ALTER TABLE provider_budget_reservations DROP COLUMN owner_machine;
                ALTER TABLE provider_budget_reservations DROP COLUMN owner_started_at;
                PRAGMA user_version=7;
                """;
            downgrade.ExecuteNonQuery();
        }
        var upgraded = new StateStore(home).Read();
        Assert(upgraded.ProviderBudgetReservations.Single().State == "held");
        Assert(GoalBudgetLedger.Available(upgraded, goal.Id) == 1488);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("A failed provider debit rolls back the matching goal debit", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-atomic-provider-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "rollback fixture" };
        var goal = new FactoryGoal { ProjectId = project.Id, TokenBudget = 4096, State = "active" };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        var fence = store.Change(s =>
        {
            s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job);
            return JobRecovery.Assign(job, "fixture", 60, local: true, state: s, reservationTokens: 1024);
        });
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}");
        db.Open();
        using var trigger = db.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER reject_provider_debit BEFORE UPDATE OF input_tokens ON provider_usages
            WHEN NEW.input_tokens > OLD.input_tokens
            BEGIN SELECT RAISE(ABORT, 'fixture rejected provider debit'); END;
            """;
        trigger.ExecuteNonQuery();
        var runtime = new BudgetedAgentRuntime(new FakeAgent(), store, job.Id, fence,
            provider: new JobProviderMeter(new ProviderProfile { TokenLimit = 1024 }, "builder", "fixture", 512));
        try
        {
            await runtime.RunAsync(home, "fixture", "", CancellationToken.None, readOnly: true);
            throw new Exception("Provider debit unexpectedly succeeded");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("fixture rejected provider debit")) { }

        var state = store.Read();
        Assert(state.Jobs.Single().TokensIn == 0 && state.Jobs.Single().TokensOut == 0);
        Assert(state.BudgetReservations.Single().Consumed == 0);
        Assert(state.ProviderUsages.Single().InputTokens == 0);
        Assert(state.ProviderBudgetReservations.Single().State == "held");

        trigger.CommandText = "DROP TRIGGER reject_provider_debit;";
        trigger.ExecuteNonQuery();
        Assert((await runtime.RunAsync(home, "retry", "", CancellationToken.None, readOnly: true)).Success);
        state = store.Read();
        Assert(state.Jobs.Single().TokensIn == 8 && state.Jobs.Single().TokensOut == 4);
        Assert(state.BudgetReservations.Single().Consumed == 12);
        Assert(state.ProviderUsages.Sum(u => u.InputTokens + u.OutputTokens) == 12);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Budget recovery links late usage after restoring a controller on another host", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-linked-recovery-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(Path.Combine(root, "source"));
        var restoredHome = Directory.CreateDirectory(Path.Combine(root, "restored")).FullName;
        var project = new ProjectInfo { Name = "Recovery fixture" };
        var goal = new FactoryGoal { ProjectId = project.Id, TokenBudget = 1500, State = "active" };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        var profile = new ProviderProfile { Id = "fixture", TokenLimit = 3000 };
        store.Change(state =>
        {
            state.Projects.Add(project); state.Goals.Add(goal); state.Jobs.Add(job);
            JobRecovery.Assign(job, "source-controller", 120, local: true, state: state, reservationTokens: 600);
            var finished = ProviderBudgetLedger.Begin(state, job, profile, "builder", "", 512);
            ProviderBudgetLedger.Record(state, finished.Id, new(40, 10));
            GoalBudgetLedger.Record(state, job, 40, 10);
            ProviderBudgetLedger.Finish(state, finished.Id, true);
            var late = ProviderBudgetLedger.Begin(state, job, profile, "review:code", "", 512);
            ProviderBudgetLedger.Record(state, late.Id, new(100, 20));
            GoalBudgetLedger.Record(state, job, 140, 30);
            state.ProviderBudgetReservations.Single(r => r.UsageId == late.Id).OwnerMachine = "retired-fixture-host";
        });
        using (var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(store.Home, "factory.sqlite3")}"))
        using (var destination = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(restoredHome, "factory.sqlite3")}"))
        {
            source.Open(); destination.Open();
            source.BackupDatabase(destination);
        }
        var restored = new StateStore(restoredHome);
        var original = restored.Read();
        var goalHold = original.BudgetReservations.Single();
        var providerHold = original.ProviderBudgetReservations.Single(r => r.State == "active");
        const string evidence = "Fixture verified old host stopped and final usage evidence";
        void Reject(Action action, string reason)
        {
            try { action(); }
            catch (InvalidOperationException ex) when (ex.Message.Contains(reason, StringComparison.OrdinalIgnoreCase)) { return; }
            throw new Exception("Expected rejection: " + reason);
        }
        Reject(() => ProviderBudgetReconciliation.HoldAfterHostRecovery(restored, providerHold.Id, true, evidence),
            "stop the associated job");
        restored.Change(s => s.Jobs.Single().LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1));
        JobRecovery.QuarantineExpired(restored);
        Assert(restored.Read().ProviderBudgetReservations.Single(r => r.Id == providerHold.Id).State == "active");
        Reject(() => ProviderBudgetReconciliation.HoldAfterHostRecovery(restored, providerHold.Id, false, evidence),
            "Verified evidence");
        ProviderBudgetReconciliation.HoldAfterHostRecovery(restored, providerHold.Id, true, evidence);
        var held = restored.Read();
        Assert(held.ProviderBudgetReservations.Single(r => r.Id == providerHold.Id).State == "held");
        Assert(held.BudgetReservations.Single().State == GoalBudgetLedger.Held &&
            GoalBudgetLedger.Available(held, goal.Id) == 900);
        Reject(() => restored.Change(s => ProviderBudgetLedger.Record(s, providerHold.UsageId, new(110, 20))),
            "no longer active");
        restored.Change(s => ProviderBudgetLedger.Finish(s, providerHold.UsageId, true));
        Assert(!restored.Read().ProviderUsages.Single(u => u.Id == providerHold.UsageId).FinalUsageReported);

        Reject(() => GoalBudgetLedger.Reconcile(restored, goal.Id, goalHold.Id, 140, 30, true, evidence),
            "outstanding provider turns");
        var lateInput = new ProviderBudgetReconciliationInput
        {
            FinalInputTokens = 560, FinalOutputTokens = 30, Reviewed = true, Reason = evidence
        };
        Reject(() => ProviderBudgetReconciliation.Reconcile(restored, providerHold.Id, lateInput), "explicitly confirm");
        Assert(restored.Read().BudgetReservations.Single().Consumed == 170 && restored.Job(job.Id).TokensIn == 140);
        lateInput.ConfirmOverrun = true;
        ProviderBudgetReconciliation.Reconcile(restored, providerHold.Id, lateInput);
        var linked = restored.Read();
        Assert(linked.ProviderBudgetReservations.Single(r => r.Id == providerHold.Id).Consumed == 590);
        Assert(linked.Jobs.Single().TokensIn == 600 && linked.Jobs.Single().TokensOut == 40);
        Assert(linked.BudgetReservations.Single().State == GoalBudgetLedger.Held &&
            linked.BudgetReservations.Single().Consumed == 640);
        Assert(linked.Events.Any(e => e.Type == "provider_budget_reconciled" && e.Message.Contains(goalHold.Id.ToString())));
        Reject(() => ProviderBudgetReconciliation.Reconcile(restored, providerHold.Id, lateInput), "Only held");
        Reject(() => GoalBudgetLedger.Reconcile(restored, goal.Id, goalHold.Id, 601, 40, true, evidence, true),
            "additional usage on its provider turn");
        Reject(() => GoalBudgetLedger.Reconcile(restored, goal.Id, goalHold.Id, 600, 40, true, evidence),
            "explicitly confirm");
        var final = GoalBudgetLedger.Reconcile(restored, goal.Id, goalHold.Id, 600, 40, true, evidence, true);
        Assert(final.State == GoalBudgetLedger.Released && GoalBudgetLedger.Available(restored.Read(), goal.Id) == 860);
        Assert(restored.Read().Events.Any(e => e.Type == "budget_reconciled" && e.Message.Contains("verified overrun 40")));
        Assert(store.Read().ProviderBudgetReservations.Single(r => r.Id == providerHold.Id).State == "active");

        restored.Change(state =>
        {
            var fresh = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
            state.Jobs.Add(fresh);
            JobRecovery.Assign(fresh, "new-controller", 120, local: true, state: state, reservationTokens: 512);
            var turn = ProviderBudgetLedger.Begin(state, fresh, new ProviderProfile(), "builder", "", 512);
            Assert(state.ProviderBudgetReservations.Single(r => r.UsageId == turn.Id).Reserved == 0);
            Assert(ProviderBudgetLedger.Record(state, turn.Id, new(80, 20)) == long.MaxValue);
        });
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

Test("Schema 8 upgrade retains usage and leaves historical cost unknown", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-cost-upgrade-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "schema 8 fixture" };
        store.Change(s =>
        {
            s.Projects.Add(project);
            var usage = ProviderBudgetLedger.Begin(s, new FactoryJob { Id = Guid.Empty, ProjectId = project.Id },
                new ProviderProfile(), "analysis", "fixture", 512);
            ProviderBudgetLedger.Record(s, usage.Id, new(8, 4));
            ProviderBudgetLedger.Finish(s, usage.Id, true);
        });
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var downgrade = db.CreateCommand();
            downgrade.CommandText = "ALTER TABLE provider_usages DROP COLUMN reported_cost_micros; PRAGMA user_version=8;";
            downgrade.ExecuteNonQuery();
        }
        var usageAfterUpgrade = new StateStore(home).Read().ProviderUsages.Single();
        Assert(usageAfterUpgrade.InputTokens == 8 && usageAfterUpgrade.OutputTokens == 4 &&
            usageAfterUpgrade.FinalUsageReported && usageAfterUpgrade.ReportedCostMicros is null);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Claude CLI uses independent read-only turns and persists reported costs", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-claude-" + Guid.NewGuid().ToString("N"));
    const string executableVariable = "FACTORY_CLAUDE_EXECUTABLE";
    var previousExecutable = Environment.GetEnvironmentVariable(executableVariable);
    try
    {
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Factory.Tests.exe" : "Factory.Tests");
        Environment.SetEnvironmentVariable(executableVariable, executable);
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "Claude fixture" };
        var goal = new FactoryGoal { ProjectId = project.Id, TokenBudget = 4096 };
        var job = new FactoryJob { ProjectId = project.Id };
        var profile = new ProviderProfile { Id = "claude-fixture", Kind = "claude", Model = "sonnet", TokenLimit = 4096 };
        var settings = new FactorySettings { Providers = [new(), profile] };
        var fence = store.Change(s =>
        {
            s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job);
            foreach (var role in new[] { "planner", "review:code" })
                s.ProviderAssignments.Add(new() { ProjectId = project.Id, Role = role, ProviderId = profile.Id });
            return JobRecovery.Assign(job, "claude-fixture", 60, local: true);
        });
        try { ProviderRouter.Validate(profile, "builder"); throw new Exception("Claude accepted a builder assignment"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("read-only")) { }

        var planner = ProviderRouter.Select(settings, store.Read(), project.Id, "planner", "medium");
        try
        {
            await planner.Runtime.RunAsync(home, "complete", Guid.NewGuid().ToString(), CancellationToken.None, readOnly: true);
            throw new Exception("Claude accepted session continuation");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("independent")) { }

        var projectRuntime = new ProjectScopedAgentRuntime(planner.Runtime, store, project.Id, goal.Id,
            profile, planner.Role, planner.Model, 512);
        Assert((await projectRuntime.RunAsync(home, "complete", "", CancellationToken.None, readOnly: true)).Success);
        try
        {
            await projectRuntime.RunAsync(home, "cost-only failure", "", CancellationToken.None, readOnly: true);
            throw new Exception("Claude accepted missing final token usage");
        }
        catch (TokenBudgetExceededException) { }

        var reviewer = ProviderRouter.Select(settings, store.Read(), project.Id, "review:code", "medium");
        var jobRuntime = new BudgetedAgentRuntime(reviewer.Runtime, store, job.Id, fence,
            provider: new JobProviderMeter(profile, reviewer.Role, reviewer.Model, 512));
        Assert((await jobRuntime.RunAsync(home, "complete", "", CancellationToken.None, readOnly: true)).Success);
        Assert((await jobRuntime.RunAsync(home, "missing cost", "", CancellationToken.None, readOnly: true)).Success);

        var persisted = new StateStore(home).Read();
        var reported = persisted.ProviderUsages.Where(u => u.JobId is null && u.FinalUsageReported).Single();
        Assert(reported.InputTokens == 13 && reported.OutputTokens == 4 && reported.ReportedCostMicros == 12345);
        var incomplete = persisted.ProviderUsages.Single(u => !u.FinalUsageReported);
        Assert(incomplete.ReportedCostMicros == 4);
        Assert(persisted.ProviderBudgetReservations.Single(r => r.UsageId == incomplete.Id).State == "held");
        Assert(persisted.ProviderUsages.Single(u => u.ReportedCostMicros is null).JobId == job.Id);
        Assert(persisted.Jobs.Single().TokensIn == 14 && persisted.Jobs.Single().TokensOut == 5);
        using var summary = System.Text.Json.JsonDocument.Parse(Json.Stringify(ProviderBudgetLedger.Summary(persisted, settings)));
        Assert(summary.RootElement[0].GetProperty("reportedCostUsd").ValueKind == System.Text.Json.JsonValueKind.Null);
        Assert(summary.RootElement[1].GetProperty("reportedCostUsd").GetDecimal() == 0.024694m);
        Assert(summary.RootElement[1].GetProperty("projects")[0].GetProperty("reportedCostUsd").GetDecimal() == 0.024694m);
    }
    finally
    {
        Environment.SetEnvironmentVariable(executableVariable, previousExecutable);
        try { Directory.Delete(home, true); } catch { }
    }
});

Test("Project review yields commit-bound findings and draft goals", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-project-review-" + Guid.NewGuid().ToString("N"));
    var repository = Path.Combine(home, "repo");
    try
    {
        Directory.CreateDirectory(repository);
        GitOperations.Git(repository, "init", "-b", "master");
        GitOperations.Git(repository, "config", "user.name", "Factory Fixture");
        GitOperations.Git(repository, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(repository, "sample.cs"), "public static class Sample {}");
        GitOperations.Git(repository, "add", "sample.cs");
        GitOperations.Git(repository, "commit", "-m", "initial");
        var store = new StateStore(Path.Combine(home, "factory"));
        var project = new ProjectInfo { Name = "review-fixture", Path = repository };
        store.Change(s => s.Projects.Add(project));
        var master = GitOperations.Head(repository);
        GitOperations.Git(repository, "checkout", "-b", "feature/review-fixture");
        File.WriteAllText(Path.Combine(repository, "README.md"), "# Feature branch README");
        GitOperations.Git(repository, "add", "README.md");
        GitOperations.Git(repository, "commit", "-m", "Add feature-only README");
        var review = ProjectReviewer.AnalyzeAsync(store, project.Id, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(review.Commit == master && review.Commit != GitOperations.Head(repository));
        Assert(review.Findings.Any(f => f.Id == "missing-readme"),
            "A feature-branch README must not count as evidence on master");
        Assert(review.Findings.Any(f => f.Id == "missing-quality-gates"));
        Assert(ProjectReviewer.Read(store, project.Id)?.Id == review.Id);
        var goal = ProjectReviewer.CreateDraftGoal(store, project.Id, new ReviewGoalInput
        {
            ReviewId = review.Id, FindingId = "missing-quality-gates", TokenBudget = 10000
        });
        Assert(goal.State == "draft" && goal.ProjectId == project.Id);
        Assert(goal.AcceptanceCriteria.Count > 0);
        var updatedReview = ProjectReviewer.AnalyzeAsync(store, project.Id, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(updatedReview.Id != review.Id);
        try
        {
            ProjectReviewer.CreateDraftGoal(store, project.Id,
                new ReviewGoalInput { ReviewId = review.Id, FindingId = "missing-quality-gates" });
            throw new Exception("Replaced review created a goal");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("replaced")) { }
        GitOperations.Git(repository, "checkout", "master");
        File.WriteAllText(Path.Combine(repository, "sample.cs"), "public static class Sample { public static int Value => 1; }");
        GitOperations.Git(repository, "add", "sample.cs");
        GitOperations.Git(repository, "commit", "-m", "advance");
        try
        {
            ProjectReviewer.CreateDraftGoal(store, project.Id,
                new ReviewGoalInput { FindingId = "missing-quality-gates" });
            throw new Exception("Stale analysis created a new goal");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("master advanced")) { }
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Master-first preflight finds existing code and preserves adoption review", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-master-preflight-" + Guid.NewGuid().ToString("N"));
    var repo = Path.Combine(root, "repo");
    Directory.CreateDirectory(repo);
    try
    {
        void Git(params string[] args) =>
            Commands.Require(Commands.Run("git", ["-C", repo, ..args], root, 30), "preflight fixture");
        Git("init", "-b", "master");
        Git("config", "user.name", "Factory Test");
        Git("config", "user.email", "test@example.invalid");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, "src", "GoalPlanner.cs"),
            "public class GoalPlanner { public void FinalizePlanning() { } }");
        Git("add", ".");
        Git("commit", "-m", "Add partial planner");
        var master = GitOperations.Head(repo);
        Git("checkout", "-b", "feature/other-work");
        File.WriteAllText(Path.Combine(repo, "src", "ShadowFeature.cs"),
            "public class ShadowFeature { }");
        Git("add", ".");
        Git("commit", "-m", "Independent feature");
        Assert(GitOperations.Head(repo) != master);

        var store = new StateStore(Path.Combine(root, "state"));
        var project = new ProjectInfo { Name = "preflight", Path = repo, BaseBranch = "master" };
        var goal = new FactoryGoal
        {
            ProjectId = project.Id, Objective = "Finalize GoalPlanner implementation",
            AcceptanceCriteria = ["GoalPlanner has verified tests"]
        };
        store.Change(state => { state.Projects.Add(project); state.Goals.Add(goal); });
        var report = WorkPackagePreflight.Inspect(store.Read(), project, goal);
        Assert(report.BaseCommit == master && report.BaseBranch == "master");
        Assert(report.ExistingCode.Any(c => c.Path == "src/GoalPlanner.cs"),
            "Preflight failed to detect existing master implementation");
        Assert(!report.MasterFiles.Contains("src/ShadowFeature.cs"),
            "Preflight used feature checkout instead of master");

        try
        {
            Planning.ProposeAsync(store, project, goal, new FakeAgent(),
                CancellationToken.None).GetAwaiter().GetResult();
            throw new Exception("Planner accepted a non-master checkout");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("clean checkout at master")) { }

        var plan = MissionControlManager.SavePlan(store, new FactoryPlan
        {
            GoalId = goal.Id, ProjectId = project.Id, GoalRevision = Planning.GoalRevision(goal),
            Packages = [new PlannedPackage
            {
                Title = "Complete existing GoalPlanner", Description = "Finish and test existing code",
                Files = ["src/GoalPlanner.cs"]
            }]
        });
        Assert(plan.BaseCommit == master && plan.Preflight is not null,
            "Manual plan was not pinned to an inspected master");
        try
        {
            Planning.Adopt(store, goal.Id, "local", planRevision: plan.Revision!.Value);
            throw new Exception("Existing implementation was adopted without human review");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("explicit review")) { }
        var adopted = Planning.Adopt(store, goal.Id, "local", reviewedExistingWork: true, planRevision: plan.Revision!.Value);
        Assert(adopted.Count == 1 && adopted[0].Title.Contains("Complete existing"));
        Assert(store.Read().Goals.Single().State == "active");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

Test("Direct job intake checks master before allowing new work", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-direct-intake-" + Guid.NewGuid().ToString("N"));
    var repo = Path.Combine(root, "repo");
    Directory.CreateDirectory(repo);
    try
    {
        void Git(params string[] args) =>
            Commands.Require(Commands.Run("git", ["-C", repo, ..args], root, 30), "job intake fixture");
        Git("init", "-b", "master");
        Git("config", "user.name", "Factory Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(repo, "GoalPlanner.cs"), "public class GoalPlanner { }");
        Git("add", "."); Git("commit", "-m", "Partial implementation");
        var store = new StateStore(Path.Combine(root, "state"));
        var project = new ProjectInfo { Name = "intake", Path = repo };
        store.Change(s => s.Projects.Add(project));
        var input = new JobInput
        {
            ProjectId = project.Id, Title = "Complete GoalPlanner", Description = "Add missing acceptance tests",
            ExpectedPaths = ["GoalPlanner.cs"]
        };
        try
        {
            MissionControlManager.CreateJob(store, input, new FactorySettings());
            throw new Exception("New job bypassed existing-code review");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("acknowledge reviewedExistingWork")) { }
        input.ReviewedExistingWork = true;
        var created = MissionControlManager.CreateJob(store, input, new FactorySettings());
        Assert(created.ProjectId == project.Id);
        Assert(store.Read().Events.Any(e => e.JobId == created.Id && e.Type == "job_preflight"),
            "Direct job preflight audit event was not persisted");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

Test("Master-first preflight rejects stale master and non-master projects", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-master-stale-" + Guid.NewGuid().ToString("N"));
    var repo = Path.Combine(root, "repo");
    Directory.CreateDirectory(repo);
    try
    {
        void Git(params string[] args) =>
            Commands.Require(Commands.Run("git", ["-C", repo, ..args], root, 30), "preflight fixture");
        Git("init", "-b", "master");
        Git("config", "user.name", "Factory Test");
        Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(repo, "README.md"), "Original");
        Git("add", ".");
        Git("commit", "-m", "Initial");
        var store = new StateStore(Path.Combine(root, "state"));
        var project = new ProjectInfo { Name = "stale", Path = repo };
        var goal = new FactoryGoal { ProjectId = project.Id, Objective = "Improve diagnostics" };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); });
        var plan = MissionControlManager.SavePlan(store, new FactoryPlan
        {
            GoalId = goal.Id, ProjectId = project.Id, GoalRevision = Planning.GoalRevision(goal),
            Packages = [new PlannedPackage { Title = "Diagnostics", Description = "Improve output",
                Files = ["README.md"] }]
        });
        try
        {
            Planning.Adopt(store, goal.Id, "local", planRevision: plan.Revision!.Value);
            throw new Exception("Plan without matches bypassed review acknowledgement");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("explicit review")) { }
        File.AppendAllText(Path.Combine(repo, "README.md"), "\nUpdate");
        Git("add", ".");
        Git("commit", "-m", "Advance master");
        try
        {
            Planning.Adopt(store, goal.Id, "local", reviewedExistingWork: true, planRevision: plan.Revision!.Value);
            throw new Exception("Stale base was accepted");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("master advanced")) { }
        project.BaseBranch = "develop";
        try
        {
            WorkPackagePreflight.MasterCommit(project);
            throw new Exception("Non-master project accepted for work-package creation");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("must be based on master")) { }
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

Test("Preflight binds the live remote master and matching PR head to the registered repository", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-remote-preflight-" + Guid.NewGuid().ToString("N"));
    var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
    var bare = Path.Combine(root, "remote.git");
    var author = Path.Combine(root, "author");
    var commandPath = Environment.GetEnvironmentVariable("PATH");
    const string fixtureVariable = "FACTORY_PREFLIGHT_GH_HEAD";
    var previousHead = Environment.GetEnvironmentVariable(fixtureVariable);
    var bin = AppContext.BaseDirectory;
    var appHost = OperatingSystem.IsWindows() ? "Factory.Tests.exe" : "Factory.Tests";
    var gh = Path.Combine(bin, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
    File.Copy(Path.Combine(bin, appHost), gh);
    try
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(gh, File.GetUnixFileMode(Path.Combine(bin, appHost)));
        Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + commandPath);
        var head = new string('a', 40);
        Environment.SetEnvironmentVariable(fixtureVariable, head);

        void Commit(string path, string title)
        {
            GitOperations.Git(path, "add", ".");
            GitOperations.Git(path, "-c", "user.name=Factory Fixture", "-c",
                "user.email=fixture@example.invalid", "commit", "-m", title);
        }
        GitOperations.Git(repo, "init", "-b", "master");
        File.WriteAllText(Path.Combine(repo, "README.md"), "# Original");
        Commit(repo, "Original");
        GitOperations.Git(root, "clone", "--bare", repo, bare);
        GitOperations.Git(repo, "remote", "add", "origin", "https://github.com/fixture/wrong-origin.git");
        GitOperations.Git(repo, "remote", "add", "upstream", "https://github.com/fixture/wrong-upstream.git");
        const string registered = "https://github.com/fixture/registered.git";
        var bareUrl = new Uri(bare).AbsoluteUri;
        GitOperations.Git(repo, "config", $"url.{bareUrl}.insteadOf", registered);

        var store = new StateStore(Path.Combine(root, "state"));
        var project = new ProjectInfo { Name = "Bound fixture", Path = repo, Origin = registered };
        var goal = new FactoryGoal { ProjectId = project.Id, Objective = "Improve diagnostics" };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); });
        var plan = MissionControlManager.SavePlan(store, new FactoryPlan
        {
            GoalId = goal.Id, ProjectId = project.Id, GoalRevision = Planning.GoalRevision(goal),
            Packages = [new() { Title = "Diagnostics", Description = "Improve diagnostics", Files = ["README.md"] }]
        });
        var original = GitOperations.Head(repo);
        Assert(plan.Preflight?.RemoteMasterStatus == "matched" && plan.Preflight.RemoteMasterCommit == original,
            "Unexpected remote master evidence: " + plan.Preflight?.RemoteMasterStatus);
        Assert(plan.Preflight?.GitHubRepository == "fixture/registered" &&
            plan.Preflight.PullRequestsChecked && plan.Preflight.IssuesChecked,
            "Inventory was not checked in the registered GitHub repository");
        Assert(plan.Preflight!.OpenPullRequests.Single().HeadCommit == head);
        var revision = plan.Revision!.Value;

        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException ex) when (ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
            throw new Exception("Expected rejection: " + message);
        }

        Environment.SetEnvironmentVariable(fixtureVariable, new string('b', 40));
        Reject(() => Planning.Adopt(store, goal.Id, "local", true, revision), "or their heads changed");
        Environment.SetEnvironmentVariable(fixtureVariable, head);

        var moved = bare + ".offline";
        Directory.Move(bare, moved);
        var unavailable = WorkPackagePreflight.Inspect(store.Read(), project, goal);
        Assert(unavailable.RemoteMasterStatus == "unavailable" && unavailable.RemoteMasterCommit is null);
        Reject(() => Planning.Adopt(store, goal.Id, "local", true, revision), "remote master could not be rechecked");
        Directory.Move(moved, bare);

        GitOperations.Git(root, "clone", bare, author);
        File.AppendAllText(Path.Combine(author, "README.md"), "\nAdvanced remote");
        Commit(author, "Advance remote");
        GitOperations.Git(author, "push", "origin", "master");
        var advanced = GitOperations.Head(author);
        var stale = WorkPackagePreflight.Inspect(store.Read(), project, goal);
        Assert(stale.RemoteMasterStatus == "mismatch" && stale.RemoteMasterCommit == advanced);
        Assert(stale.BaseCommit == original && GitOperations.Head(repo) == original);
        Assert(!Commands.Run("git", ["-C", repo, "show-ref", "--verify", "--quiet", "refs/remotes/origin/master"], repo).Ok,
            "Live verification wrote a local remote-tracking ref");
        Reject(() => Planning.Adopt(store, goal.Id, "local", true, revision), "Local master differs");
        Reject(() => WorkPackagePreflight.InspectDirectJob(store.Read(), project, "Diagnostics", "Update diagnostics"),
            "Local master differs");
        Assert(store.Read().Jobs.Count == 0);
        var workspace = Path.Combine(root, "worktrees");
        Reject(() => new GitOperations(workspace).Prepare(project, new FactoryJob { ProjectId = project.Id }),
            "Local master differs");
        Assert(!Directory.Exists(workspace) || !Directory.EnumerateDirectories(workspace).Any(),
            "A worktree was created from stale master");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", commandPath);
        Environment.SetEnvironmentVariable(fixtureVariable, previousHead);
        File.Delete(gh);
        try { Directory.Delete(root, true); } catch { }
    }
});

await TestAsync("Plan adoption requires the reviewed plan and current goal revisions", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-plan-revisions-" + Guid.NewGuid().ToString("N"));
    var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
    try
    {
        void Git(params string[] args) => GitOperations.Git(repo, args);
        Git("init", "-b", "master");
        File.WriteAllText(Path.Combine(repo, "README.md"), "# Fixture");
        Git("add", ".");
        Git("-c", "user.name=Factory Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "Initial");

        var store = new StateStore(Path.Combine(root, "state"));
        var project = new ProjectInfo { Name = "Plan revisions", Path = repo };
        var goal = new FactoryGoal { ProjectId = project.Id, Objective = "Original diagnostics", AcceptanceCriteria = ["Old criterion"] };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); });

        FactoryPlan Draft(FactoryGoal snapshot, string title, Guid? revision = null) => new()
        {
            GoalId = snapshot.Id, ProjectId = snapshot.ProjectId, Revision = revision,
            GoalRevision = Planning.GoalRevision(snapshot),
            Packages = [new() { Title = title, Description = "Reviewed fixture package", Files = ["README.md"] }]
        };
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException ex) when (ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
            throw new Exception("Expected rejection: " + message);
        }

        var legacy = Draft(goal, "Legacy package");
        legacy.GoalRevision = "";
        var path = Planning.PathFor(store, goal.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Json.Stringify(legacy));
        Reject(() => Planning.Adopt(store, goal.Id, "local", true), "predates revision tracking");
        var original = MissionControlManager.SavePlan(store, Draft(goal, "Original package"));
        Assert(original.Revision is not null && original.GoalRevision == Planning.GoalRevision(goal));
        var revisedGoal = MissionControlManager.UpdateGoal(store, goal.Id, new GoalInput
        {
            ProjectId = project.Id, Objective = goal.Objective, AcceptanceCriteria = ["New criterion"], TokenBudget = 100000
        });
        Reject(() => Planning.Adopt(store, goal.Id, "local", true, original.Revision!.Value), "Goal changed");
        Reject(() => MissionControlManager.SavePlan(store, Draft(goal, "Old editor", original.Revision)), "Goal changed");
        Assert(store.Read().Jobs.Count == 0);

        var revised = MissionControlManager.SavePlan(store, Draft(revisedGoal, "Revised package", original.Revision));
        Reject(() => Planning.Adopt(store, goal.Id, "local", true, original.Revision!.Value), "current plan revision");
        Reject(() => Planning.Adopt(store, goal.Id, "local", true), "current plan revision");
        Reject(() => MissionControlManager.SavePlan(store, Draft(revisedGoal, "Stale edit", original.Revision)), "Plan changed");

        var paused = new PausedPlanAgent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var generating = Planning.ProposeAsync(store, project, revisedGoal, paused, timeout.Token, revised.Revision);
        await Task.WhenAny(paused.Entered.Task, generating).WaitAsync(timeout.Token);
        Assert(paused.Entered.Task.IsCompletedSuccessfully, "Generation did not reach the fixture runtime");
        var latest = MissionControlManager.SavePlan(store, Draft(revisedGoal, "Saved during generation", revised.Revision));
        paused.Resume.SetResult();
        try
        {
            await generating;
            throw new Exception("Generation overwrote a newer manual plan");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Plan changed")) { }
        Assert(MissionControlManager.ReadPlan(store, goal.Id)?.Revision == latest.Revision);
        var withBudgetChange = MissionControlManager.UpdateGoal(store, goal.Id, new GoalInput
        {
            ProjectId = project.Id, Objective = revisedGoal.Objective,
            AcceptanceCriteria = revisedGoal.AcceptanceCriteria, TokenBudget = 120000
        });
        Assert(Planning.GoalRevision(withBudgetChange) == latest.GoalRevision);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var edit = Task.Run(async () =>
        {
            await gate.Task;
            try
            {
                MissionControlManager.UpdateGoal(store, goal.Id, new GoalInput
                {
                    ProjectId = project.Id, Objective = "Concurrent objective", AcceptanceCriteria = ["New criterion"], TokenBudget = 100000
                });
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("retain their adopted objective")) { return false; }
        });
        var adoption = Task.Run(async () =>
        {
            await gate.Task;
            try { return Planning.Adopt(store, goal.Id, "local", true, latest.Revision!.Value).Count; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Goal changed")) { return 0; }
        });
        gate.SetResult();
        await Task.WhenAll(edit, adoption).WaitAsync(timeout.Token);
        var state = store.Read();
        Assert(edit.Result ? adoption.Result == 0 && state.Jobs.Count == 0 :
            adoption.Result == 1 && state.Jobs.Single().Title == "Saved during generation" &&
            state.Goals.Single().Objective == revisedGoal.Objective);
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

await TestAsync("Review requires exact passing verdicts and a complete checked patch", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-review-gate-" + Guid.NewGuid().ToString("N"));
    var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
    try
    {
        void Git(params string[] args) => GitOperations.Git(repo, args);
        void Commit(string message)
        {
            Git("add", ".");
            Git("-c", "user.name=Factory Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", message);
        }
        Git("init", "-b", "master");
        File.WriteAllText(Path.Combine(repo, "README.md"), "Original context\n");
        Commit("Base");
        var baseHead = GitOperations.Head(repo);
        Git("switch", "-c", "feature/review");
        File.WriteAllText(Path.Combine(repo, "README.md"), "Actual reviewed change\n");
        Commit("Review patch");
        var head = GitOperations.Head(repo);
        Git("config", "diff.external", "nonexistent-factory-fixture-diff");

        var job = new FactoryJob { Worktree = repo, Description = "Review fixture" };
        var project = new ProjectInfo { BaseBranch = "master" };
        QualityResult Report(string? sha = null) => new() { Passed = true, HeadSha = sha ?? head };
        var good = new ReviewFixtureAgent("VERDICT: PASS\r\nReviewed README.md");
        var strict = new QualityEngine(good);
        var invalid = new Dictionary<string, ReviewFixtureAgent>
        {
            ["code"] = new("VERDICT: PASS WITH ISSUES\nIncomplete"),
            ["architecture"] = new("\nVERDICT: PASS\nLater line"),
            ["security"] = new("VERDICT: PASS\r\nTool failure", success: false),
            ["ux"] = new("VERDICT: PASS \nTrailing space")
        };
        var badReport = await new QualityEngine(good, role => invalid[role])
            .ReviewAsync(project, job, Report(), CancellationToken.None);
        Assert(!badReport.Passed && badReport.Reviews.Count == 4 &&
            badReport.Reviews.Values.All(v => v.StartsWith("CHANGES\n", StringComparison.Ordinal)));

        var fallback = new ReviewFixtureAgent("VERDICT: PASS", fallback: true);
        var fallbackReport = await new QualityEngine(fallback).ReviewAsync(project, job, Report(), CancellationToken.None);
        Assert(!fallbackReport.Passed && fallbackReport.Reviews.Values.All(v => v.StartsWith("CHANGES\n", StringComparison.Ordinal)));

        var valid = await strict.ReviewAsync(project, job, Report(), CancellationToken.None);
        Assert(valid.Passed && valid.Reviews.Count == 4 && good.Prompts.Count == 4);
        Assert(good.Prompts.All(p => p.Contains("+Actual reviewed change")),
            "Review did not receive the raw checked patch");

        async Task RejectBeforeReview(string reason, string? sha = null)
        {
            var calls = good.Prompts.Count;
            var report = await strict.ReviewAsync(project, job, Report(sha), CancellationToken.None);
            Assert(!report.Passed && report.Reviews.Count == 0 &&
                report.Findings.Any(f => f.Contains(reason, StringComparison.Ordinal)), reason);
            Assert(good.Prompts.Count == calls, "A reviewer ran with unusable patch context");
        }
        await RejectBeforeReview("does not match the commit", baseHead);
        project.BaseBranch = "missing";
        await RejectBeforeReview("Unable to obtain review diff");
        project.BaseBranch = "feature/review";
        await RejectBeforeReview("No committed patch");
        project.BaseBranch = "master";

        File.WriteAllBytes(Path.Combine(repo, "fixture.bin"), [0, 1, 2, 3, 4]);
        Commit("Binary patch");
        await RejectBeforeReview("binary changes", GitOperations.Head(repo));
        Git("switch", "--detach", head);

        File.WriteAllText(Path.Combine(repo, "large.txt"), new string('x', 105000));
        Commit("Oversized patch");
        await RejectBeforeReview("exceeds supported full-context size", GitOperations.Head(repo));

        Git("switch", "--detach", head);
        var moved = false;
        var movingReviewer = new ReviewFixtureAgent("VERDICT: PASS", onReview: () =>
        {
            if (moved) return;
            moved = true;
            Git("switch", "--detach", baseHead);
        });
        var changedHead = await new QualityEngine(movingReviewer).ReviewAsync(project, job, Report(), CancellationToken.None);
        Assert(!changedHead.Passed && changedHead.Findings.Any(f => f.Contains("HEAD changed while")));
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

Test("Job quality inspector validates commit identity and file boundary", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-quality-view-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "quality" };
        var head = new string('a', 40);
        var job = new FactoryJob { ProjectId = project.Id,
            State = JobStates.CompletedLocal, HeadSha = head };
        var evidenceFolder = Path.Combine(home, "evidence", job.Id.ToString("N"));
        Directory.CreateDirectory(evidenceFolder);
        var path = Path.Combine(evidenceFolder, head + ".json");
        File.WriteAllText(path, Json.Stringify(new
        {
            jobId = job.Id, commit = head, report = new QualityResult
            {
                HeadSha = head, Passed = true,
                Checks = [new CheckReport { Name = "build", ExitCode = 0 }],
                Reviews = new Dictionary<string, string> { ["code"] = "PASS\\nReviewed" }
            }
        }));
        job.EvidenceFile = path;
        store.Change(s => { s.Projects.Add(project); s.Jobs.Add(job); });
        using (var parsed = System.Text.Json.JsonDocument.Parse(
            Json.Stringify(JobEvidenceReader.Read(store, job.Id))))
        {
            var root = parsed.RootElement;
            Assert(root.GetProperty("current").GetBoolean());
            Assert(root.GetProperty("status").GetString() == "passed");
            Assert(root.GetProperty("checks").GetArrayLength() == 1);
            Assert(root.GetProperty("reviews").GetArrayLength() == 1);
        }
        store.Change(s => s.Jobs.Single().HeadSha = new string('b', 40));
        using (var parsed = System.Text.Json.JsonDocument.Parse(
            Json.Stringify(JobEvidenceReader.Read(store, job.Id))))
            Assert(parsed.RootElement.GetProperty("status").GetString() == "stale");
        store.Change(s => s.Jobs.Single().EvidenceFile = Path.Combine(home, "secret.json"));
        try
        {
            _ = JobEvidenceReader.Read(store, job.Id);
            throw new Exception("Quality inspector read a path outside its job evidence directory");
        }
        catch (InvalidOperationException) { }
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Mission Control embeds operational recovery and budget views", () =>
{
    string Resource(string resource)
    {
        using var stream = typeof(DashboardHost).Assembly.GetManifestResourceStream(resource)
            ?? throw new Exception("Missing embedded UI asset " + resource);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    var html = Resource("Factory.Ui.Index");
    var script = Resource("Factory.Ui.Script");
    var style = Resource("Factory.Ui.Style");
    Assert(html.Contains("data-view=\"analysis\"", StringComparison.Ordinal));
    Assert(script.Contains("startProjectReview()", StringComparison.Ordinal));
    Assert(script.Contains("formProjectProviders(id)", StringComparison.Ordinal));
    Assert(script.Contains("formProviderReconciliation(id)", StringComparison.Ordinal));
    Assert(html.Contains("id=\"brand-connection\"", StringComparison.Ordinal));
    Assert(html.Contains("id=\"brand-connection-dot\"", StringComparison.Ordinal));
    Assert(!html.Contains("id=\"connection-pill\"", StringComparison.Ordinal));
    Assert(!html.Contains("id=\"open-connection\"", StringComparison.Ordinal));
    Assert(html.IndexOf("id=\"top-create\"", StringComparison.Ordinal) <
        html.IndexOf("id=\"open-search\"", StringComparison.Ordinal));
    Assert(html.Contains("id=\"top-create\" aria-label=\"Create new\"", StringComparison.Ordinal));
    Assert(script.Contains("$('brand-connection').addEventListener", StringComparison.Ordinal));
    Assert(!script.Contains("$('connection-pill')", StringComparison.Ordinal));
    Assert(style.Contains("@keyframes brand-heartbeat", StringComparison.Ordinal));
    Assert(style.Contains("prefers-reduced-motion:reduce", StringComparison.Ordinal));
    Assert(script.Contains("planRevision:revision", StringComparison.Ordinal));
    Assert(script.Contains("reviewedExistingWork", StringComparison.Ordinal),
        "Direct job intake acknowledgement is not exposed in Mission Control");
    Assert(html.Contains("data-view=\"recovery\"", StringComparison.Ordinal));
    Assert(script.Contains("renderRecovery()", StringComparison.Ordinal));
    Assert(script.Contains("renderGoalBudget(goalId)", StringComparison.Ordinal));
    Assert(script.Contains("goals/'+goalId+'/budget", StringComparison.Ordinal));
    Assert(script.Contains("budget/reconcile", StringComparison.Ordinal));
    Assert(script.Contains("jobInspectorDetails(j,kv)", StringComparison.Ordinal));
    Assert(script.Contains("renderJobQuality(jobId)", StringComparison.Ordinal));
    Assert(script.Contains("jobs/'+jobId+'/quality", StringComparison.Ordinal));
    Assert(script.Contains("Not verified for current job head", StringComparison.Ordinal));
    Assert(script.Contains("Per-job reported usage", StringComparison.Ordinal));
    Assert(script.Contains("Controller-authorized extension history", StringComparison.Ordinal));
    Assert(script.Contains("goalExtensionIncrementTokens", StringComparison.Ordinal));
    Assert(script.Contains("maxJobReservationTokens", StringComparison.Ordinal));
    Assert(script.Contains("Dedicated extension history is unavailable", StringComparison.Ordinal));
    Assert(script.Contains("Registered worker connectivity", StringComparison.Ordinal));
    Assert(script.Contains("not measured scheduling latency", StringComparison.Ordinal));
    Assert(style.Contains(".operational-pulse", StringComparison.Ordinal));
    Assert(style.Contains(".budget-breakdown", StringComparison.Ordinal));
});

Test("Path normalization", () => Assert(GitOperations.SamePath(".", Path.GetFullPath("."))));
Test("Agent executable locator picks first existing binary", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "factory-agent-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var missing = Path.Combine(directory, "missing.exe");
        var first = Path.Combine(directory, "first.exe");
        var second = Path.Combine(directory, "second.exe");
        File.WriteAllText(first, "");
        File.WriteAllText(second, "");
        Assert(Commands.FirstExistingExecutable([missing, first, second]) == Path.GetFullPath(first));
        Assert(Commands.FirstExistingExecutable([missing]) is null);
    }
    finally { Directory.Delete(directory, recursive: true); }
});
Test("Explicit native executable override resolves and validates", () =>
{
    var old = Environment.GetEnvironmentVariable("FACTORY_AGENT_EXECUTABLE");
    var directory = Path.Combine(Path.GetTempPath(), "factory-agent-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var executable = Path.Combine(directory, "codex.exe");
    try
    {
        File.WriteAllText(executable, "");
        Environment.SetEnvironmentVariable("FACTORY_AGENT_EXECUTABLE", executable);
        Assert(Commands.ResolveAgentExecutable() == Path.GetFullPath(executable));
        Assert(Commands.StartInfo("codex", ["app-server"], directory).FileName == Path.GetFullPath(executable));
        Environment.SetEnvironmentVariable("FACTORY_AGENT_EXECUTABLE", executable + ".missing");
        try { Commands.ResolveAgentExecutable(); throw new Exception("Missing override was accepted"); }
        catch (FileNotFoundException ex) { Assert(ex.Message.Contains("FACTORY_AGENT_EXECUTABLE")); }
    }
    finally
    {
        Environment.SetEnvironmentVariable("FACTORY_AGENT_EXECUTABLE", old);
        Directory.Delete(directory, recursive: true);
    }
});

Test("Cycle detection", () =>
{
    var a = new FactoryJob(); var b = new FactoryJob();
    a.DependsOn.Add(b.Id); b.DependsOn.Add(a.Id);
    Assert(DependencyGraph.WouldCycle([a, b]));
});
Test("Dependency integration gate", () =>
{
    var a = new FactoryJob(); var b = new FactoryJob { DependsOn = [a.Id] };
    Assert(!DependencyGraph.DependenciesSatisfied(b, [a,b]));
    a.Integrated = true;
    Assert(DependencyGraph.DependenciesSatisfied(b, [a,b]));
});
Test("Unknown files conflict", () =>
{
    var id = Guid.NewGuid(); var a = new FactoryJob { ProjectId = id, ExpectedPaths = [] };
    var b = new FactoryJob { ProjectId = id, ExpectedPaths = ["src/main.cs"] };
    Assert(DependencyGraph.Conflict(a,b));
});
Test("Safe parallel jobs", () =>
{
    var id = Guid.NewGuid();
    var a = new FactoryJob { ProjectId = id, ExpectedPaths = ["src/x.cs"] };
    var b = new FactoryJob { ProjectId = id, ExpectedPaths = ["docs/readme.md"] };
    Assert(!DependencyGraph.Conflict(a,b));
});
Test("Scheduler respects project parallel limit", () =>
{
    var p = new ProjectInfo { MaxParallel = 1 };
    var a = new FactoryJob { ProjectId = p.Id, ExpectedPaths = ["a"] };
    var b = new FactoryJob { ProjectId = p.Id, ExpectedPaths = ["b"] };
    var state = new FactoryState { Projects = [p], Jobs = [a,b] };
    Assert(JobScheduler.Select(state, 5).Count == 1);
});

Test("Scheduler enforces execution mode before priority and capacity", () =>
{
    var project = new ProjectInfo { MaxParallel = 1 };
    var remote = new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Remote,
        Priority = 100, ExpectedPaths = ["src/remote.cs"] };
    var local = new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Local,
        Priority = 1, ExpectedPaths = ["src/local.cs"] };
    var invalid = new FactoryJob { ProjectId = project.Id, Mode = "unknown",
        Priority = 200, ExpectedPaths = ["src/unknown.cs"] };
    var state = new FactoryState { Projects = [project], Jobs = [remote, local, invalid] };
    Assert(JobScheduler.Select(state, 1).Single().Id == local.Id,
        "Default scheduling selected a remote or unsupported job");
    Assert(JobScheduler.Select(state, 1, JobExecutionModes.Local).Single().Id == local.Id);
    Assert(JobScheduler.Select(state, 1, JobExecutionModes.Remote).Single().Id == remote.Id);
    try
    {
        JobScheduler.Select(state, 1, "unknown");
        throw new Exception("Unsupported runner mode was accepted");
    }
    catch (ArgumentException) { }
});

Test("Store atomic persistence", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var p = new ProjectInfo { Name = "test" };
        store.Change(s => s.Projects.Add(p));
        Assert(new StateStore(home).Read().Projects.Single().Id == p.Id);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});
Test("Planning rejects cycle", () =>
{
    var plan = new FactoryPlan { Packages = new List<PlannedPackage>
    {
        new PlannedPackage { Title="A", Description="a", Files=["src/a.cs"], DependsOn=[1] },
        new PlannedPackage { Title="B", Description="b", Files=["src/b.cs"], DependsOn=[0] }
    } };
    try { Planning.Validate(plan); throw new Exception("Cycle not rejected"); }
    catch (ArgumentException) { }
});
Test("Unauthenticated worker blocked", () => Assert(!WorkerProtocol.Authorized("Bearer invalid", "secret")));
Test("Authorized worker accepted", () => Assert(WorkerProtocol.Authorized("Bearer abc", "abc")));

Test("Child processes do not inherit controller or worker API tokens", () =>
{
    var oldAdmin = Environment.GetEnvironmentVariable("FACTORY_TOKEN");
    var oldWorker = Environment.GetEnvironmentVariable("FACTORY_WORKER_TOKEN");
    try
    {
        Environment.SetEnvironmentVariable("FACTORY_TOKEN", "admin-test-secret");
        Environment.SetEnvironmentVariable("FACTORY_WORKER_TOKEN", "worker-test-secret");
        var child = Commands.StartInfo("git", ["--version"], Environment.CurrentDirectory);
        Assert(!child.Environment.ContainsKey("FACTORY_TOKEN"));
        Assert(!child.Environment.ContainsKey("FACTORY_WORKER_TOKEN"));
    }
    finally
    {
        Environment.SetEnvironmentVariable("FACTORY_TOKEN", oldAdmin);
        Environment.SetEnvironmentVariable("FACTORY_WORKER_TOKEN", oldWorker);
    }
});

Test("Controller and worker secrets are generated independently and remain stable", () =>
{
    var settings = new FactorySettings { ApiToken = "existing-admin-token" };
    Assert(settings.EnsureCredentials());
    Assert(settings.ApiToken == "existing-admin-token");
    Assert(WorkerAuthentication.IsValidKey(settings.WorkerTokenKey));
    Assert(settings.WorkerTokenKey != settings.ApiToken);
    var key = settings.WorkerTokenKey;
    Assert(!settings.EnsureCredentials());
    Assert(settings.WorkerTokenKey == key);
    var legacy = new FactorySettings { ApiToken = key, WorkerTokenKey = key };
    Assert(legacy.EnsureCredentials() && legacy.WorkerTokenKey != legacy.ApiToken);
});

Test("Worker credentials cannot authenticate to administrator endpoints", () =>
{
    var settings = new FactorySettings();
    settings.EnsureCredentials();
    var token = WorkerAuthentication.CreateToken(settings.WorkerTokenKey, "builder-01");
    string Bearer(string value) => "Bearer " + value;
    Assert(WorkerAuthentication.Authorized("POST", "/api/worker/claim", Bearer(token), "builder-01", settings));
    foreach (var path in new[] { "/api/state", "/api/settings", "/api/jobs", "/api/workers/register",
        "/api/workers/builder-01/action", "/api/queue/run", "/api/usage" })
        Assert(!WorkerAuthentication.Authorized("GET", path, Bearer(token), "builder-01", settings),
            "Worker reached admin route: " + path);
    Assert(WorkerAuthentication.Authorized("GET", "/api/state", Bearer(settings.ApiToken), null, settings));
    Assert(!WorkerAuthentication.Authorized("POST", "/api/worker/claim",
        Bearer(settings.ApiToken), "builder-01", settings));
    Assert(!WorkerAuthentication.Authorized("PUT", "/api/workers/builder-01/permissions",
        Bearer(token), "builder-01", settings), "Worker token could elevate its own grants");
    Assert(WorkerAuthentication.Authorized("PUT", "/api/workers/builder-01/permissions",
        Bearer(settings.ApiToken), null, settings));
});

Test("Worker credentials are bound to identities and worker routes fail closed", () =>
{
    var settings = new FactorySettings();
    settings.EnsureCredentials();
    var a = WorkerAuthentication.CreateToken(settings.WorkerTokenKey, "builder-01");
    var b = WorkerAuthentication.CreateToken(settings.WorkerTokenKey, "builder-02");
    Assert(a != b);
    Assert(WorkerAuthentication.Authorized("POST", "/api/worker/heartbeat/", "Bearer " + a, "builder-01", settings));
    Assert(!WorkerAuthentication.Authorized("POST", "/api/worker/heartbeat", "Bearer " + a, "builder-02", settings));
    Assert(!WorkerAuthentication.Authorized("GET", "/api/worker/heartbeat", "Bearer " + a, "builder-01", settings));
    Assert(!WorkerAuthentication.Authorized("POST", "/api/worker/other", "Bearer " + a, "builder-01", settings));
    Assert(!WorkerAuthentication.Authorized("POST", "/api/worker/claim", "Bearer " + a, null, settings));
    Assert(!WorkerAuthentication.MatchesIdentity("builder-01", "builder-02"));
    Assert(WorkerAuthentication.MatchesIdentity("builder-01", "builder-01"));
    Assert(!WorkerAuthentication.IsValidWorkerId("../builder-01"));
    Assert(!WorkerAuthentication.IsValidWorkerId("worker:admin"));
});

Test("Unregistered and disabled workers cannot claim jobs", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-auth-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var settings = new FactorySettings { AllowPush = true };
        Assert(WorkerProtocol.Claim(store, settings, "unknown-worker") is null);
        MissionControlManager.RegisterWorker(store, new WorkerInput { Id = "disabled-worker" });
        MissionControlManager.ControlWorker(store, "disabled-worker", "disable");
        Assert(WorkerProtocol.Claim(store, settings, "disabled-worker") is null);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Remote claims cannot cross project grants or gain access through registration", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-project-auth-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var a = new ProjectInfo { Name = "allowed", Origin = "https://example.invalid/a" };
        var b = new ProjectInfo { Name = "forbidden", Origin = "https://example.invalid/b" };
        var authorizedJob = new FactoryJob { ProjectId = a.Id, Mode = JobExecutionModes.Remote,
            Priority = 10, ExpectedPaths = ["src/a.cs"] };
        var forbiddenJob = new FactoryJob { ProjectId = b.Id, Mode = JobExecutionModes.Remote,
            Priority = 100, ExpectedPaths = ["src/b.cs"] };
        store.Change(state =>
        {
            state.Projects.Add(a); state.Projects.Add(b);
            state.Jobs.Add(authorizedJob); state.Jobs.Add(forbiddenJob);
        });
        var settings = new FactorySettings { AllowPush = true };
        MissionControlManager.RegisterWorker(store, new WorkerInput { Id = "scoped-worker" });
        Assert(WorkerProtocol.Claim(store, settings, "scoped-worker") is null,
            "Registration implicitly granted access");

        GrantRemoteWorker(store, "scoped-worker", a.Id);
        var grantSnapshot = new StateStore(home).Read();
        Assert(grantSnapshot.WorkerProjectGrants.Single().ProjectId == a.Id);
        Assert(grantSnapshot.WorkerCapabilityProofs.Count == 6,
            "Trusted capability evidence was not persisted");

        // Re-registration is under worker credentials, not administrator privileges.
        MissionControlManager.RegisterWorker(store,
            new WorkerInput { Id = "scoped-worker", Platform = "spoofed-platform" });
        Assert(store.Read().WorkerProjectGrants.Count == 1 &&
            store.Read().WorkerProjectGrants.Single().ProjectId == a.Id,
            "Re-registration changed project grants");

        var claim = WorkerProtocol.Claim(store, settings, "scoped-worker");
        Assert(claim is not null && claim.Job.Id == authorizedJob.Id &&
            claim.Project.Id == a.Id, "Claim escaped authorized project");
        Assert(store.Job(forbiddenJob.Id).State == JobStates.Queued &&
            store.Job(forbiddenJob.Id).Attempt == 0, "Foreign project job was reserved");
        Assert(WorkerProtocol.Claim(store, settings, "scoped-worker") is null,
            "Worker claimed an unauthorized project after exhausting its queue");

        MissionControlManager.RegisterWorker(store, new WorkerInput { Id = "other-worker" });
        GrantRemoteWorker(store, "other-worker", b.Id);
        var forged = new WorkerStatus
        {
            WorkerId = "other-worker", JobId = authorizedJob.Id,
            ExecutionGeneration = claim!.Job.ExecutionGeneration,
            LeaseToken = claim.Job.LeaseToken
        };
        Assert(!WorkerProtocol.Heartbeat(store, forged, settings),
            "Cross-project heartbeat updated another worker's lease");
        Assert(!WorkerProtocol.CompleteAsync(store, forged,
            new RefusingWorkerVerifier()).GetAwaiter().GetResult(),
            "Cross-project completion was accepted");

        // Revocation affects existing leases as well as future claims.
        MissionControlManager.SetWorkerPermissions(store, "scoped-worker",
            new WorkerPermissionsInput
            {
                Reason = "Operator revoked access to the project"
            });
        var formerLease = new WorkerStatus
        {
            WorkerId = "scoped-worker", JobId = authorizedJob.Id,
            ExecutionGeneration = claim.Job.ExecutionGeneration,
            LeaseToken = claim.Job.LeaseToken
        };
        Assert(!WorkerProtocol.Heartbeat(store, formerLease, settings),
            "Revoked worker retained heartbeat privileges");
        Assert(!WorkerProtocol.CompleteAsync(store, formerLease,
            new RefusingWorkerVerifier()).GetAwaiter().GetResult(),
            "Revoked worker completed a previously assigned job");
        Assert(store.Job(authorizedJob.Id).State != JobStates.PrReady);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Remote execution requires current trusted capability evidence", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-capability-auth-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo
        {
            Name = "isolated-checks", Origin = "https://example.invalid/c",
            RequiredExecutionCapabilities = ["container-checks"],
            Checks = [new CheckCommand
            {
                Name = "container", Executable = "dotnet", ContainerImage = "example/dotnet:latest"
            }]
        };
        var job = new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Remote,
            RequiredExecutionCapabilities = ["container-checks"] };
        store.Change(state =>
        {
            state.Projects.Add(project); state.Jobs.Add(job);
            state.Workers.Add(new WorkerInfo { Id = "limited-worker" });
        });
        var settings = new FactorySettings { AllowPush = true };
        MissionControlManager.SetWorkerPermissions(store, "limited-worker", new WorkerPermissionsInput
        {
            ProjectIds = [project.Id], Reason = "Verified only native worker capabilities",
            VerifiedCapabilities =
            [
                new() { Capability = "native-agent", Evidence = "Verified native execution manually" },
                new() { Capability = "git-cli", Evidence = "Verified Git CLI manually" },
                new() { Capability = "runtime:exec", Evidence = "Verified exec adapter manually" }
            ]
        });
        Assert(WorkerProtocol.Claim(store, settings, "limited-worker") is null,
            "Worker without required container capability took job");

        GrantRemoteWorker(store, "limited-worker", project.Id);
        store.Change(state => state.WorkerCapabilityProofs.Single(p =>
            p.WorkerId == "limited-worker" && p.Capability == "container-checks")
            .VerifiedAt = DateTimeOffset.UtcNow.AddDays(-31));
        Assert(WorkerProtocol.Claim(store, settings, "limited-worker") is null,
            "Expired capability verification was trusted");

        GrantRemoteWorker(store, "limited-worker", project.Id);
        Assert(WorkerProtocol.Claim(store, settings, "limited-worker") is null,
            "Unchanged evidence text improperly renewed an expired attestation");
        var evidence = store.Read().WorkerCapabilityProofs
            .Where(p => p.WorkerId == "limited-worker")
            .Select(p => new VerifiedWorkerCapabilityInput
            {
                Capability = p.Capability,
                Evidence = p.Capability == "container-checks"
                    ? p.Evidence + "; reverified after expiration" : p.Evidence
            }).ToList();
        MissionControlManager.SetWorkerPermissions(store, "limited-worker", new WorkerPermissionsInput
        {
            ProjectIds = [project.Id],
            Reason = "Operator reverified expired container capability",
            VerifiedCapabilities = evidence
        });
        Assert(WorkerProtocol.Claim(store, settings, "limited-worker")?.Job.Id == job.Id,
            "Reverified worker was not allowed to claim an eligible job");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Unknown worker capabilities and foreign grants are rejected", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-grants-invalid-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        MissionControlManager.RegisterWorker(store, new WorkerInput { Id = "untrusted-worker" });
        void Reject(WorkerPermissionsInput input)
        {
            try
            {
                MissionControlManager.SetWorkerPermissions(store, "untrusted-worker", input);
                throw new Exception("Unsafe worker grant accepted");
            }
            catch (InvalidOperationException) { }
        }
        Reject(new WorkerPermissionsInput
        {
            ProjectIds = [Guid.NewGuid()], Reason = "Unknown project must not be granted"
        });
        Reject(new WorkerPermissionsInput
        {
            Reason = "Capability has no actual controller evidence",
            VerifiedCapabilities = [new() { Capability = "native-agent" }]
        });
        Reject(new WorkerPermissionsInput
        {
            Reason = "Unverifiable requested isolation capability",
            VerifiedCapabilities = [new() { Capability = "isolated-agent", Evidence = "Unsupported mode" }]
        });
        Assert(store.Read().WorkerProjectGrants.Count == 0 &&
            store.Read().WorkerCapabilityProofs.Count == 0, "Invalid grants were persisted");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Remote provider leases fence route, model, budget and completion evidence",
    RemoteProviderLeaseTests.RunAsync);
await TestAsync("Remote provider verification rejects a force-push during the commit tree lookup",
    RemoteProviderLeaseTests.VerifyMovingPullHeadAsync);
await TestAsync("Remote restore preserves holds, fences replay and accepts only the rotated executor credential",
    RemoteRestoreTests.RunAsync);
await TestAsync("Project viewers can only read their granted projects and redacted quality evidence",
    ProjectViewerTests.RunAsync);
await TestAsync("Project planners and operators enforce scoped writes and atomic audit",
    ProjectPlanningTests.RunAsync);
await TestAsync("Project plan revisions and reviewed adoption enforce scope and atomic audit",
    ScopedPlanTests.RunAsync);

Test("Remote claims never take local jobs", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-mode-claim-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "mode-claim", Origin = "https://example.invalid/repo" };
        var local = new FactoryJob { ProjectId = project.Id, Priority = 100,
            Mode = JobExecutionModes.Local, ExpectedPaths = ["src/local.cs"] };
        var remote = new FactoryJob { ProjectId = project.Id, Priority = 1,
            Mode = JobExecutionModes.Remote, ExpectedPaths = ["src/remote.cs"] };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Jobs.Add(local); s.Jobs.Add(remote);
            s.Workers.Add(new WorkerInfo { Id = "remote-worker" });
        });
        GrantRemoteWorker(store, "remote-worker", project.Id);
        var claim = WorkerProtocol.Claim(store, new FactorySettings { AllowPush = true }, "remote-worker");
        Assert(claim?.Job.Id == remote.Id, "Remote worker did not select its eligible job");
        Assert(store.Job(local.Id).State == JobStates.Queued && store.Job(local.Id).Attempt == 0,
            "Remote worker claimed a local job");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Scope rules reject missing paths, traversal, and prefix collisions", () =>
{
    void Reject(Action action)
    {
        try { action(); throw new Exception("Unsafe scope accepted"); }
        catch (ScopeViolationException) { }
    }
    Reject(() => WorkPackageScope.ValidateDeclaration([]));
    foreach (var bad in new[] { "../source.cs", "src/../private.cs", "/absolute.cs",
        "C:\\absolute.cs", "src//file.cs", ".", "src/*.cs", "src/./file.cs" })
        Reject(() => WorkPackageScope.ValidateDeclaration([bad]));
    WorkPackageScope.Enforce(["src/Feature.cs", "docs/"], ["src/feature.cs", "docs/guide.md"]);
    Assert(WorkPackageScope.OutOfScope(["src/Feature.cs"],
        ["src/Feature.cs.bak", "src/Feature.cs"]).SequenceEqual(["src/Feature.cs.bak"]));
    Reject(() => WorkPackageScope.Enforce(["src/"], ["docs/README.md"]));
});

Test("Git scope scanner includes committed, staged, unstaged, renamed and untracked paths", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-scope-git-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    void Git(params string[] args) =>
        Commands.Require(Commands.Run("git", ["-C", root, ..args], root, 90), "git test");
    try
    {
        Git("init", "-b", "master");
        Git("config", "user.email", "factory-tests@example.invalid");
        Git("config", "user.name", "Factory Tests");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "old.cs"), "old");
        File.WriteAllText(Path.Combine(root, "src", "tracked.cs"), "tracked");
        Git("add", "."); Git("commit", "-m", "Initial");
        Git("checkout", "-b", "feature");
        Git("mv", "src/old.cs", "src/new.cs");
        Git("commit", "-m", "Rename");
        File.AppendAllText(Path.Combine(root, "src", "tracked.cs"), "change");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "created.md"), "new");
        var paths = WorkPackageScope.ChangedPaths(root, "master");
        foreach (var expected in new[] { "src/old.cs", "src/new.cs", "src/tracked.cs",
            "docs/created.md" })
            Assert(paths.Contains(expected), "Missing Git change: " + expected);
        Assert(WorkPackageScope.OutOfScope(["src/"], paths).SequenceEqual(["docs/created.md"]));
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

await TestAsync("Remote scope block reaches the controller as amendable scope", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-scope-remote-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "remote", Origin = "https://github.com/example/repo" };
        var job = new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Remote,
            ExpectedPaths = ["src/allowed.cs"] };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Jobs.Add(job);
            s.Workers.Add(new WorkerInfo { Id = "worker-01" });
        });
        GrantRemoteWorker(store, "worker-01", project.Id);
        var claim = WorkerProtocol.Claim(store, new FactorySettings { AllowPush = true }, "worker-01");
        Assert(claim is not null);
        var reported = new FactoryJob
        {
            Id = job.Id, ProjectId = project.Id, Attempt = claim!.Job.Attempt,
            Mode = JobExecutionModes.Remote, State = JobStates.Blocked, Stage = "scope",
            Error = "Contract amendment required for src/unexpected.cs"
        };
        Assert(await WorkerProtocol.CompleteAsync(store, new WorkerStatus
        {
            WorkerId = "worker-01", JobId = job.Id, Job = reported,
            ExecutionGeneration = claim.Job.ExecutionGeneration, LeaseToken = claim.Job.LeaseToken,
            Error = reported.Error
        }, new RefusingWorkerVerifier()));
        var blocked = store.Job(job.Id);
        Assert(blocked.State == JobStates.Blocked && blocked.Stage == "scope" &&
            blocked.Error.Contains("src/unexpected.cs", StringComparison.Ordinal),
            "Remote scope violation was not preserved for review");
        Assert(store.Read().Events.Any(e => e.JobId == job.Id && e.Type == "scope_violation"));
        MissionControlManager.AmendScope(store, job.Id, new ScopeAmendmentInput
        {
            ExpectedPaths = ["src/allowed.cs", "src/unexpected.cs"],
            Reason = "Remote worktree inspected and scope expansion explicitly approved",
            Reviewed = true
        });
        Assert(store.Job(job.Id).Stage == "scope_amended");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Controller PR file verification includes renamed source and destination", () =>
{
    var changed = GitHubWorkerResultVerifier.ParsePullFilePaths(
        """[[{"filename":"src/new.cs","previous_filename":"src/old.cs"},{"filename":"docs/readme.md"}]]""");
    Assert(changed.Count == 3);
    Assert(WorkPackageScope.OutOfScope(["src/new.cs", "docs/"], changed)
        .SequenceEqual(["src/old.cs"]));
    try
    {
        GitHubWorkerResultVerifier.ParsePullFilePaths("[]");
        throw new Exception("Empty PR file listing was accepted");
    }
    catch (InvalidOperationException) { }
});

Test("Protected path gate", () =>
{
    try { GitOperations.EnforcePaths(["factory/checks.json"],["factory/"]); throw new Exception("Not blocked"); }
    catch (InvalidOperationException) { }
});
Test("No implicit merge permission", () => Assert(!new FactorySettings().AutoMerge && !new FactorySettings().AllowPush));

Test("Weekly allowance parsed from account response", () =>
{
    const string response = """
    {"result":{"rateLimits":{"primary":{"usedPercent":24,"windowDurationMins":300,"resetsAt":1794000000},
      "secondary":{"usedPercent":67.5,"windowDurationMins":10080,"resetsAt":1794100000}}}}
    """;
    var snapshot = UsageMonitor.ParseResponse(response);
    Assert(snapshot.Weekly != null && Math.Abs(snapshot.Weekly.UsedPercent - 67.5) < 0.001);
    Assert(snapshot.Weekly != null && Math.Abs(snapshot.Weekly.RemainingPercent - 32.5) < 0.001);
    Assert(snapshot.Weekly?.WindowMinutes == 10080);
    Assert(snapshot.Weekly?.ResetsAt?.ToUnixTimeSeconds() == 1794100000);
    Assert(snapshot.ShortWindow?.UsedPercent == 24);
});
Test("Weekly allowance unavailable must not be inferred", () =>
{
    var snapshot = UsageMonitor.ParseResponse("""
    {"result":{"rateLimits":{"primary":{"usedPercent":51,"windowDurationMins":300}}}}
    """);
    Assert(snapshot.Weekly == null);
    Assert(snapshot.ShortWindow?.RemainingPercent == 49);
});
Test("Alternate account bucket and snake case supported", () =>
{
    var snapshot = UsageMonitor.ParseResponse("""
    {"result":{"rateLimitsByLimitId":{"account":{"secondary":{"used_percent":8.0,
       "window_minutes":10080,"resets_at":1794100000}}}}}
    """);
    Assert(snapshot.Weekly?.RemainingPercent == 92);
});
Test("Invalid usage percentages are rejected", () =>
{
    var snapshot = UsageMonitor.ParseResponse("""
    {"result":{"rateLimits":{"secondary":{"usedPercent":120,"windowDurationMins":10080}}}}
    """);
    Assert(snapshot.Weekly == null);
});



async Task TestAsync(string name, Func<Task> test)
{
    try { await test(); results.Add((name, true, "")); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { results.Add((name, false, ex.Message)); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}


await TestAsync("Local queue cannot claim or execute remote jobs", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-mode-queue-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "local-controller", MaxParallel = 2 };
        var remote = new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Remote };
        store.Change(s => { s.Projects.Add(project); s.Jobs.Add(remote); });
        var engine = new FactoryEngine(store, new FactorySettings { MaxParallel = 2 },
            agent: new FakeAgent());
        await engine.RunQueueAsync(CancellationToken.None);
        var unchanged = store.Job(remote.Id);
        Assert(unchanged.State == JobStates.Queued && unchanged.Attempt == 0 &&
            string.IsNullOrEmpty(unchanged.LeaseOwner), "Local queue claimed a remote job");
        Assert(!store.Read().Events.Any(e => e.JobId == remote.Id && e.Type == "claimed"),
            "Local runner emitted a claim for a remote job");
        try
        {
            await engine.RunClaimedAsync(remote.Id, CancellationToken.None);
            throw new Exception("Unclaimed remote job was executed by local runner");
        }
        catch (InvalidOperationException ex)
        {
            Assert(ex.Message.Contains("not claimed", StringComparison.Ordinal));
        }
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Scope violation blocks before commit and requires approved amendment", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-scope-e2e-" + Guid.NewGuid().ToString("N"));
    var repo = Path.Combine(root, "repository");
    Directory.CreateDirectory(repo);
    string Git(params string[] args) =>
        Commands.Require(Commands.Run("git", ["-C", repo, ..args], root, 90), "git fixture");
    try
    {
        Git("init", "-b", "master");
        Git("config", "user.email", "factory-tests@example.invalid");
        Git("config", "user.name", "Factory Tests");
        File.WriteAllText(Path.Combine(repo, "README.md"), "Initial");
        Git("add", "README.md"); Git("commit", "-m", "Initial");
        var state = new StateStore(Path.Combine(root, "state"));
        var project = new ProjectInfo { Name = "scope", Path = repo, BaseBranch = "master",
            Checks = [new CheckCommand { Name = "git", Executable = "git",
                Arguments = ["rev-parse", "HEAD"] }] };
        state.Change(s => s.Projects.Add(project));
        var job = state.AddJob(new FactoryJob { ProjectId = project.Id, Title = "Bounded work",
            Description = "Write one source file", ExpectedPaths = ["docs/"] });
        var engine = new FactoryEngine(state, new FactorySettings(), agent: new FakeAgent());
        await engine.RunQueueAsync(CancellationToken.None);
        var blocked = state.Job(job.Id);
        Assert(blocked.State == JobStates.Blocked && blocked.Stage == "scope",
            blocked.Error);
        Assert(blocked.HeadSha == "" && blocked.PrUrl == "",
            "Out-of-scope changes were committed or published");
        Assert(File.Exists(Path.Combine(blocked.Worktree, "src", "generated.txt")),
            "Blocked worktree was unexpectedly discarded");
        Assert(state.Read().Events.Any(e => e.JobId == job.Id && e.Type == "scope_violation"));
        var originalHead = Git("rev-parse", "master");
        Assert(GitOperations.Head(blocked.Worktree) == originalHead,
            "Blocked change modified Git history");

        void Reject(Action action)
        {
            try { action(); throw new Exception("Invalid scope amendment was accepted"); }
            catch (InvalidOperationException) { }
        }
        Reject(() => MissionControlManager.ControlJob(state, job.Id,
            new ControlInput { Action = "retry", Reviewed = true }));
        Reject(() => MissionControlManager.AmendScope(state, job.Id,
            new ScopeAmendmentInput { ExpectedPaths = ["src/generated.txt"],
                Reason = "Approved file expansion", Reviewed = false }));
        Reject(() => MissionControlManager.AmendScope(state, job.Id,
            new ScopeAmendmentInput { ExpectedPaths = ["../private"],
                Reason = "Approved file expansion", Reviewed = true }));
        var edited = new JobInput { ProjectId = project.Id, Title = "Bounded work",
            Description = "Write one source file", ExpectedPaths = ["src/generated.txt"] };
        Reject(() => MissionControlManager.UpdateJob(state, job.Id, edited));

        // Explicit approval remains separate from retry and requires worktree review.
        MissionControlManager.AmendScope(state, job.Id, new ScopeAmendmentInput
        {
            ExpectedPaths = ["src/generated.txt"],
            Reason = "Reviewed generated source and approved ownership expansion",
            Reviewed = true
        });
        Assert(state.Job(job.Id).State == JobStates.Blocked &&
            state.Job(job.Id).Stage == "scope_amended", "Amendment started execution");
        Assert(state.Read().Events.Any(e => e.JobId == job.Id && e.Type == "scope_amended"),
            "Missing scope amendment audit");
        MissionControlManager.ControlJob(state, job.Id,
            new ControlInput { Action = "retry", Reviewed = true, Reason = "Review completed" });
        await engine.RunQueueAsync(CancellationToken.None);
        Assert(state.Job(job.Id).State == JobStates.CompletedLocal,
            state.Job(job.Id).Error);
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

await TestAsync("Local agent pipeline, evidence and verified integration", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-e2e-" + Guid.NewGuid().ToString("N"));
    var repo = Path.Combine(root, "repository");
    Directory.CreateDirectory(repo);
    try
    {
        void Git(params string[] arg) => Commands.Require(Commands.Run("git", ["-C", repo, ..arg], root, 90), "git test");
        Git("init", "-b", "master");
        Git("config", "user.email", "factory-tests@example.invalid");
        Git("config", "user.name", "Factory Tests");
        File.WriteAllText(Path.Combine(repo, "README.md"), "Initial source");
        Git("add", "README.md");
        Git("commit", "-m", "Initial commit");
        var store = new StateStore(Path.Combine(root, "state"));
        var project = new ProjectInfo
        {
            Name = "test", Path = repo, BaseBranch = "master", MaxParallel = 2,
            Checks = [new CheckCommand { Name = "git", Executable = "git", Arguments = ["rev-parse", "HEAD"] }]
        };
        store.Change(state => state.Projects.Add(project));

        // A remote-context engine must remain able to execute remote jobs.
        var remoteJob = store.AddJob(new FactoryJob
        {
            ProjectId = project.Id, Title = "Remote execution", Mode = JobExecutionModes.Remote,
            ExpectedPaths = ["src/generated.txt"]
        });
        var remoteEngine = new FactoryEngine(store, new FactorySettings { MaxParallel = 1 },
            agent: new FakeAgent(), executionMode: JobExecutionModes.Remote);
        await remoteEngine.RunQueueAsync(CancellationToken.None);
        Assert(store.Job(remoteJob.Id).State == JobStates.CompletedLocal,
            "Remote-context engine failed to execute a remote job");
        var job = store.AddJob(new FactoryJob
        {
            ProjectId = project.Id, Title = "Implement bounded change", Description = "Write one text file",
            ExpectedPaths = ["src/generated.txt"]
        });
        var engine = new FactoryEngine(store, new FactorySettings { MaxParallel = 1, MaxReviewRounds = 2 },
            agent: new FakeAgent());
        await engine.RunQueueAsync(CancellationToken.None);
        var completed = store.Job(job.Id);
        Assert(completed.State == JobStates.CompletedLocal, completed.Error);
        Assert(File.Exists(completed.EvidenceFile), "Evidence missing");
        Assert(!completed.Integrated, "Premature integration");
        Git("merge", "--no-ff", completed.Branch, "-m", "Integrate change");
        await engine.VerifyIntegrationAsync(job.Id);
        Assert(store.Job(job.Id).Integrated, "Verified merge not recorded");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});


await TestAsync("Remote integration rejects unpushed local history and stale local bases", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-integration-" + Guid.NewGuid().ToString("N"));
    var checkout = Path.Combine(root, "checkout");
    var origin = Path.Combine(root, "origin.git");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(checkout);
    void Git(string dir, params string[] args) =>
        Commands.Require(Commands.Run("git", ["-C", dir, ..args], root, 90), "git fixture");
    try
    {
        Commands.Require(Commands.Run("git", ["init", "--bare", "-b", "master", origin], root), "git init bare");
        Git(checkout, "init", "-b", "master");
        Git(checkout, "config", "user.name", "Factory Tests");
        Git(checkout, "config", "user.email", "factory-tests@example.invalid");
        File.WriteAllText(Path.Combine(checkout, "README.md"), "Initial");
        Git(checkout, "add", "README.md"); Git(checkout, "commit", "-m", "Initial");
        Git(checkout, "remote", "add", "origin", origin);
        Git(checkout, "push", "-u", "origin", "master");

        File.WriteAllText(Path.Combine(checkout, "README.md"), "Unpublished change");
        Git(checkout, "add", "README.md"); Git(checkout, "commit", "-m", "Change");
        var unpublished = GitOperations.Head(checkout);
        var store = new StateStore(Path.Combine(root, "state"));
        var project = new ProjectInfo { Name = "remote", Path = checkout, Origin = origin };
        var job = new FactoryJob { ProjectId = project.Id, State = JobStates.CompletedLocal,
            Mode = JobExecutionModes.Local, HeadSha = unpublished };
        store.Change(s => { s.Projects.Add(project); s.Jobs.Add(job); });
        var engine = new FactoryEngine(store, new FactorySettings(), agent: new FakeAgent());
        async Task Reject(Guid id)
        {
            try
            {
                await engine.VerifyIntegrationAsync(id);
                throw new Exception("Unverified integration was accepted");
            }
            catch (InvalidOperationException) { }
            Assert(!store.Job(id).Integrated, "Blocked integration released dependent work");
            Assert(!store.Read().Events.Any(e => e.JobId == id && e.Type == "integrated"));
        }
        await Reject(job.Id);
        Git(checkout, "push", "origin", "master");
        var proof = await engine.VerifyIntegrationAsync(job.Id);
        Assert(proof.Source == "remote-ancestry" && proof.IntegratedCommit == unpublished);
        Assert(store.Job(job.Id).Integrated);

        var other = Path.Combine(root, "other");
        Commands.Require(Commands.Run("git", ["clone", origin, other], root), "git clone");
        Git(other, "config", "user.name", "Factory Tests");
        Git(other, "config", "user.email", "factory-tests@example.invalid");
        File.WriteAllText(Path.Combine(other, "other.txt"), "Merged elsewhere");
        Git(other, "add", "other.txt"); Git(other, "commit", "-m", "Remote only");
        var remoteCommit = GitOperations.Head(other);
        Git(other, "push", "origin", "master");
        var stale = store.AddJob(new FactoryJob { ProjectId = project.Id,
            Mode = JobExecutionModes.Local, State = JobStates.CompletedLocal,
            HeadSha = remoteCommit });
        await Reject(stale.Id);
        Git(checkout, "pull", "--ff-only", "origin", "master");
        var refreshed = await engine.VerifyIntegrationAsync(stale.Id);
        Assert(refreshed.BaseCommit == remoteCommit && refreshed.IntegratedCommit == remoteCommit);
        Assert(DependencyGraph.DependenciesSatisfied(new FactoryJob { DependsOn = [stale.Id] },
            store.Read().Jobs), "Verified remote integration did not release dependent work");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

await TestAsync("Merged squash PR requires matching metadata and reachable remote merge commit", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-squash-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    void Git(params string[] args) =>
        Commands.Require(Commands.Run("git", ["-C", root, ..args], root, 90), "git fixture");
    try
    {
        Git("init", "-b", "master");
        Git("config", "user.name", "Factory Tests");
        Git("config", "user.email", "factory-tests@example.invalid");
        File.WriteAllText(Path.Combine(root, "README.md"), "Initial");
        Git("add", "README.md"); Git("commit", "-m", "Initial");
        Git("checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(root, "feature.txt"), "Feature");
        Git("add", "feature.txt"); Git("commit", "-m", "Feature");
        var sourceSha = GitOperations.Head(root);
        Git("checkout", "master");
        Git("merge", "--squash", "feature");
        Git("commit", "-m", "Squash feature");
        var mergeSha = GitOperations.Head(root);
        Assert(!IntegrationVerifier.IsAncestor(root, sourceSha, mergeSha),
            "Fixture must simulate rewritten commit history");

        var project = new ProjectInfo { Path = root, Origin = "https://github.com/example/repository",
            BaseBranch = "master" };
        var job = new FactoryJob { ProjectId = project.Id, State = JobStates.PrReady,
            Mode = JobExecutionModes.Remote, HeadSha = sourceSha,
            PrUrl = "https://github.com/example/repository/pull/12" };
        job.Branch = $"factory/{job.Id:N}";
        var merged = new MergedPullRequest(true, "example/repository", "example/repository",
            "master", job.Branch, sourceSha, mergeSha);
        var remote = new FixedRemoteBaseReader(mergeSha);
        var proof = await IntegrationVerifier.VerifyAsync(project, job, remote,
            new FixedIntegrationPullReader(merged));
        Assert(proof.Source == "remote-merged-pr" && proof.IntegratedCommit == mergeSha);

        async Task Reject(MergedPullRequest candidate)
        {
            try
            {
                await IntegrationVerifier.VerifyAsync(project, job, remote,
                    new FixedIntegrationPullReader(candidate));
                throw new Exception("Invalid merged PR was accepted");
            }
            catch (InvalidOperationException) { }
        }
        await Reject(merged with { Merged = false });
        await Reject(merged with { BaseBranch = "main" });
        await Reject(merged with { HeadRepository = "attacker/fork" });
        await Reject(merged with { HeadCommit = new string('a', 40) });
        await Reject(merged with { MergeCommit = sourceSha });
        try
        {
            await IntegrationVerifier.VerifyAsync(project, job,
                new FixedRemoteBaseReader(new string('b', 40)),
                new FixedIntegrationPullReader(merged));
            throw new Exception("Unreachable remote base was accepted");
        }
        catch (InvalidOperationException) { }
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

Test("Mission Control worker registration does not spoof heartbeat", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-ui-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var worker = MissionControlManager.RegisterWorker(store, new WorkerInput { Id = "worker-a", Platform = "windows" });
        Assert(worker.Id == "worker-a" && worker.Heartbeat == DateTimeOffset.MinValue);
        Assert(WorkerProtocol.Heartbeat(store, new WorkerStatus { WorkerId = "worker-a" }, new FactorySettings()));
        Assert((DateTimeOffset.UtcNow - store.Read().Workers.Single().Heartbeat).TotalSeconds < 10);
        MissionControlManager.ControlWorker(store, "worker-a", "drain");
        Assert(store.Read().Workers.Single().Draining);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});
Test("Mission Control goal lifecycle and audit history", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-ui-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "Sample" };
        store.Change(s => s.Projects.Add(project));
        var goal = MissionControlManager.CreateGoal(store, new GoalInput { ProjectId = project.Id,
            Objective = "Improve source reliability", TokenBudget = 30000 });
        Assert(goal.State == "draft");
        MissionControlManager.SetGoalStatus(store, goal.Id, "activate");
        Assert(store.Read().Goals.Single().State == "active");
        MissionControlManager.SetGoalStatus(store, goal.Id, "pause");
        Assert(store.Read().Goals.Single().State == "paused");
        Assert(store.Read().Events.Count >= 3);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});
Test("Mission Control job cannot skip worktree inspection after interrupted execution", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-ui-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "Sample" };
        store.Change(s => s.Projects.Add(project));
        var job = MissionControlManager.CreateJob(store, new JobInput { ProjectId = project.Id,
            Title = "Refine module", Description = "Refactor carefully",
            ExpectedPaths = ["src/module.cs"] }, new FactorySettings());
        MissionControlManager.ControlJob(store, job.Id, new ControlInput { Action = "pause" });
        MissionControlManager.ControlJob(store, job.Id, new ControlInput { Action = "resume" });
        store.UpdateJob(job.Id, j => { j.State = JobStates.Paused; j.StartedAt = DateTimeOffset.UtcNow; });
        try { MissionControlManager.ControlJob(store, job.Id, new ControlInput { Action = "resume" }); throw new Exception("Unsafe resume accepted"); }
        catch (InvalidOperationException) { }
        MissionControlManager.ControlJob(store, job.Id, new ControlInput { Action = "resume", Reviewed = true });
        Assert(store.Job(job.Id).State == JobStates.Queued);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});
Test("Project status gates scheduler", () =>
{
    var p = new ProjectInfo { Name = "Sample", Status = "paused" };
    var j = new FactoryJob { ProjectId = p.Id };
    Assert(JobScheduler.Select(new FactoryState { Projects = [p], Jobs = [j] }, 4).Count == 0);
    p.Status = "active";
    Assert(JobScheduler.Select(new FactoryState { Projects = [p], Jobs = [j] }, 4).Count == 1);
});
Test("Mission Control blocks cross-project job dependencies", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-ui-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var a = new ProjectInfo { Name = "a" }; var b = new ProjectInfo { Name = "b" };
        store.Change(s => { s.Projects.Add(a); s.Projects.Add(b); });
        var other = store.AddJob(new FactoryJob { ProjectId = b.Id, Title = "B" });
        try
        {
            MissionControlManager.CreateJob(store, new JobInput { ProjectId = a.Id, Title = "A", Description = "Test",
                DependsOn = [other.Id], ExpectedPaths = ["src/a.cs"] }, new FactorySettings());
            throw new Exception("Cross-project dependency accepted");
        }
        catch (ArgumentException) { }
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});


Test("Native SQLite version includes CVE-2025-6965 fix", () =>
{
    using var database = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    database.Open();
    using var query = database.CreateCommand();
    query.CommandText = "SELECT sqlite_version();";
    var versionText = (string?)query.ExecuteScalar();
    Assert(Version.TryParse(versionText, out var nativeVersion) &&
        nativeVersion >= new Version(3, 50, 2),
        "Loaded native SQLite version is vulnerable or could not be determined: " + versionText);
});

Test("Relational SQLite schema contains no JSON state table", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-relational-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = Path.Combine(home, "factory.sqlite3"), ForeignKeys = true }.ToString());
        db.Open();
        long Count(string sql)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        Assert(Count("PRAGMA user_version;") == 18, "Unexpected relational schema version");
        Assert(Count("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='factory_state';") == 0);
        foreach (var table in new[] { "factory_metadata", "projects", "goals", "jobs", "events",
            "workers", "worker_project_grants", "worker_capability_proofs",
            "projects_required_execution_capabilities", "jobs_required_execution_capabilities",
            "releases", "knowledge_items", "graph_nodes", "evaluations",
            "project_checks", "project_check_arguments", "jobs_depends_on",
            "projects_protected_paths", "goals_acceptance_criteria", "evaluations_failures" })
            Assert(Count($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';") == 1,
                "Missing relational table: " + table);
        Assert(Count("SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name='project_id';") == 1);
        Assert(Count("SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name='state';") == 1);
        foreach (var field in new[] { "execution_generation", "lease_token", "worker_process_id",
            "worker_process_started_at", "worker_machine_name", "checkpoint_stage", "checkpoint_commit", "checkpoint_at" })
            Assert(Count($"SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name='{field}';") == 1,
                "Missing durable recovery field: " + field);
        Assert(Count("SELECT COUNT(*) FROM pragma_foreign_key_list('jobs') WHERE \"table\"='projects';") == 1);
        Assert(Count("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_jobs_project_state';") == 1);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Relational persistence roundtrips all entities and ordered collections", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-relational-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo
        {
            Name = "my-project", Path = "C:/src/repo", Origin = "https://example.invalid/repo",
            Checks = [new CheckCommand { Name = "build", Executable = "dotnet",
                Arguments = ["build", "--configuration", "Release"], TimeoutSeconds = 45 }],
            ProtectedPaths = [".github/", "configs/"]
        };
        var goal = new FactoryGoal { ProjectId = project.Id, Objective = "Improve quality",
            AcceptanceCriteria = ["Build works", "Tests pass"], TokenBudget = 12345 };
        var first = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id,
            Title = "first", ExpectedPaths = ["src/A.cs"], State = JobStates.Integrated,
            Integrated = true, TokensIn = 42 };
        var second = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id,
            Title = "second", DependsOn = [first.Id], ExpectedPaths = ["src/B.cs"],
            LeaseUntil = DateTimeOffset.Parse("2026-01-01T10:20:30+02:00"),
            StartedAt = DateTimeOffset.Parse("2026-01-01T09:00:00+00:00") };
        var entry = new FactoryEvent { JobId = first.Id, Type = "status", Message = "ready" };
        store.Change(s =>
        {
            s.Schema = 1; s.Paused = true;
            s.Projects.Add(project); s.Goals.Add(goal);
            s.Jobs.Add(first); s.Jobs.Add(second);
            s.Events.Add(entry);
            s.Knowledge.Add(new KnowledgeItem { ProjectId = project.Id,
                Topic = "arch", Content = "Use layered services", SourceSha = "sha" });
            s.Graph.Add(new GraphNode { ProjectId = project.Id, File = "src/A.cs",
                Imports = ["System", "Factory"], Symbols = ["Alpha"], Hash = "file-hash" });
            s.Evaluations.Add(new EvaluationResult { Suite = "regression", Passed = 2,
                Total = 3, Failures = ["Missing edge case", "Needs retry"] });
            s.Releases.Add(new ReleaseInfo { ProjectId = project.Id, Version = "1.2.3",
                Commit = "abcdef", ManifestPath = "releases/manifest.json" });
            s.Workers.Add(new WorkerInfo { Id = "worker-01", Enabled = true, Draining = true,
                Platform = "windows", Heartbeat = DateTimeOffset.Parse("2026-01-01T08:30:00+01:00") });
        });
        var data = new StateStore(home).Read();
        Assert(data.Schema == 1 && data.Paused);
        Assert(data.Projects.Count == 1 && data.Projects[0].Id == project.Id);
        Assert(data.Projects[0].Checks.Count == 1 &&
            data.Projects[0].Checks[0].Arguments.SequenceEqual(["build", "--configuration", "Release"]));
        Assert(data.Projects[0].ProtectedPaths.SequenceEqual([".github/", "configs/"]));
        Assert(data.Goals.Single().AcceptanceCriteria.SequenceEqual(["Build works", "Tests pass"]));
        Assert(data.Goals.Single().TokenBudget == 12345);
        Assert(data.Jobs.Count == 2 && data.Jobs[1].DependsOn.SequenceEqual([first.Id]));
        Assert(data.Jobs[1].ExpectedPaths.SequenceEqual(["src/B.cs"]));
        Assert(data.Jobs[1].LeaseUntil == second.LeaseUntil && data.Jobs[1].StartedAt == second.StartedAt);
        Assert(data.Events.Count == 1 && data.Events[0].Id == entry.Id);
        Assert(data.Knowledge.Single().Content == "Use layered services");
        Assert(data.Graph.Single().Imports.SequenceEqual(["System", "Factory"]) &&
            data.Graph.Single().Symbols.SequenceEqual(["Alpha"]));
        Assert(data.Evaluations.Single().Failures.SequenceEqual(["Missing edge case", "Needs retry"]));
        Assert(data.Releases.Single().Version == "1.2.3");
        Assert(data.Workers.Single().Draining && data.Workers.Single().Id == "worker-01");
        store.UpdateJob(second.Id, j => { j.State = JobStates.Reviewing; j.TokensOut = 22; });
        Assert(new StateStore(home).Job(second.Id).TokensOut == 22);
        Assert(new StateStore(home).Read().Projects.Single().Checks[0].Arguments.Count == 3);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("State mutations update only modified relational rows", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-relational-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "stable" };
        var job = new FactoryJob { ProjectId = project.Id, Title = "work" };
        store.Change(s => { s.Projects.Add(project); s.Jobs.Add(job); });
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TRIGGER reject_unchanged_project_update
                BEFORE UPDATE ON projects BEGIN
                    SELECT RAISE(ABORT, 'unchanged project was rewritten');
                END;
                """;
            cmd.ExecuteNonQuery();
        }
        store.UpdateJob(job.Id, j => j.State = JobStates.Building);
        Assert(store.Job(job.Id).State == JobStates.Building);
        Assert(store.Project(project.Name).Id == project.Id);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Relational transactions rollback failed mutations and foreign key violations", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-relational-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "clean" };
        store.Change(s => s.Projects.Add(project));
        try
        {
            store.Change(s => { s.Projects.Single().Status = "disabled"; throw new InvalidOperationException("abort"); });
            throw new Exception("Mutation unexpectedly committed");
        }
        catch (InvalidOperationException ex) { Assert(ex.Message == "abort"); }
        Assert(store.Project(project.Name).Status == "active");
        try
        {
            store.Change(s => s.Jobs.Add(new FactoryJob { ProjectId = Guid.NewGuid(), Title = "orphan" }));
            throw new Exception("Foreign-key-invalid job was saved");
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        Assert(store.Read().Jobs.Count == 0 && store.Read().Projects.Count == 1);
        var first = store.AddJob(new FactoryJob { ProjectId = project.Id, Title = "valid" });
        try
        {
            store.Change(s => s.Projects.Clear());
            throw new Exception("Project deletion bypassed job foreign key");
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        Assert(store.Read().Projects.Count == 1 && store.Job(first.Id).ProjectId == project.Id);
        Assert(store.Job(first.Id).Id == first.Id);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Legacy JSON state is rejected without modifying the database", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-relational-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(home);
    try
    {
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE factory_state(id INTEGER PRIMARY KEY, content TEXT NOT NULL);
                INSERT INTO factory_state(id,content) VALUES(1,'{"projects":[]}');
                """;
            cmd.ExecuteNonQuery();
        }
        try { _ = new StateStore(home); throw new Exception("Legacy database was accepted"); }
        catch (InvalidOperationException ex) { Assert(ex.Message.Contains("legacy", StringComparison.OrdinalIgnoreCase)); }
        using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(home, "factory.sqlite3")}");
        verify.Open();
        using var check = verify.CreateCommand();
        check.CommandText = "SELECT content FROM factory_state WHERE id=1;";
        Assert((string?)check.ExecuteScalar() == "{\"projects\":[]}");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Relational state supports atomic concurrent writes", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-relational-" + Guid.NewGuid().ToString("N"));
    try
    {
        _ = new StateStore(home);
        var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            new StateStore(home).Event(null, "concurrent", "event-" + i))).ToArray();
        Task.WhenAll(tasks).GetAwaiter().GetResult();
        var messages = new StateStore(home).Read().Events.Select(x => x.Message).ToHashSet();
        Assert(messages.Count == tasks.Length && messages.Contains("event-0") && messages.Contains("event-11"));
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});


Test("Targeted SQLite operations preserve execution fencing and job children", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-targeted-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "targeted" };
        var job = new FactoryJob { ProjectId = project.Id, ExpectedPaths = ["src/", "tests/"],
            RequiredExecutionCapabilities = ["native-agent"] };
        var worker = new WorkerInfo { Id = "heartbeat-target" };
        store.Change(s => { s.Projects.Add(project); s.Jobs.Add(job); s.Workers.Add(worker); });
        var fence = store.Change(s => JobRecovery.Assign(
            s.Jobs.Single(j => j.Id == job.Id), "target-worker", 120, local: true));
        var loaded = store.Job(job.Id);
        Assert(loaded.ExpectedPaths.SequenceEqual(job.ExpectedPaths));
        Assert(loaded.RequiredExecutionCapabilities.SequenceEqual(job.RequiredExecutionCapabilities));
        Assert(store.RenewLease(job.Id, fence, 180), "Live fenced lease renewal failed");
        Assert(!store.RenewLease(job.Id, fence with { Generation = fence.Generation - 1 }, 180),
            "Superseded generation renewed an execution lease");
        Assert(!store.RenewLease(job.Id, fence with { Owner = "other-worker" }, 180),
            "Foreign worker renewed an execution lease");
        Assert(store.TouchWorkerHeartbeat(worker.Id));
        Assert((DateTimeOffset.UtcNow - store.Read().Workers.Single().Heartbeat).TotalSeconds < 10);
        store.Change(s => s.Workers.Single().Enabled = false);
        Assert(!store.TouchWorkerHeartbeat(worker.Id), "Disabled worker updated heartbeat");
        store.Change(s => s.Jobs.Single().LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert(!store.RenewLease(job.Id, fence, 120), "Expired lease renewed");
        store.Event(job.Id, "targeted_test", "persisted without aggregate mutation");
        Assert(store.Read().Events.Any(e => e.JobId == job.Id &&
            e.Type == "targeted_test"), "Targeted event was not committed");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Fenced stage transition writes job and event in one transaction", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-stage-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "stage-test" };
        var job = new FactoryJob { ProjectId = project.Id };
        store.Change(s => { s.Projects.Add(project); s.Jobs.Add(job); });
        var fence = store.Change(s => JobRecovery.Assign(s.Jobs.Single(), "stage-runner", 120, local: true));
        store.SetFencedStage(job.Id, fence, JobStates.Building, "agent");
        var snapshot = store.Read();
        var actual = snapshot.Jobs.Single();
        Assert(actual.State == JobStates.Building && actual.Stage == "agent" &&
            actual.CheckpointStage == "agent" && actual.CheckpointAt is not null);
        Assert(snapshot.Events.Count(e => e.JobId == job.Id && e.Type == "stage" &&
            e.Message == "building/agent") == 1);

        try
        {
            store.SetFencedStage(job.Id, fence with { Generation = fence.Generation - 1 },
                JobStates.Failed, "stale");
            throw new Exception("Superseded generation mutated the live job");
        }
        catch (LostExecutionLeaseException) { }
        Assert(store.Job(job.Id).State == JobStates.Building);
        Assert(store.Read().Events.Count(e => e.Type == "stage") == 1);

        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_stage_event BEFORE INSERT ON events
                WHEN NEW.type='stage' BEGIN SELECT RAISE(ABORT, 'stage event rejected'); END;
                """;
            command.ExecuteNonQuery();
        }
        try
        {
            store.SetFencedStage(job.Id, fence, JobStates.Reviewing, "review");
            throw new Exception("Stage mutation survived failed audit insert");
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        Assert(store.Job(job.Id).State == JobStates.Building &&
            store.Job(job.Id).CheckpointStage == "agent",
            "Failed audit event must roll back the job transition");
        store.Change(s => s.Jobs.Single().LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1));
        try
        {
            store.SetFencedStage(job.Id, fence, JobStates.Reviewing, "review");
            throw new Exception("Expired execution changed its stage");
        }
        catch (LostExecutionLeaseException) { }
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Fenced token debits are atomic, additive and crash-safe", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-usage-sql-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "usage-fence" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 800 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job); });
        var fence = store.Change(s => JobRecovery.Assign(s.Jobs.Single(),
            "usage-worker", 120, local: true, state: s, reservationTokens: 512));

        Assert(store.RecordFencedUsage(job.Id, fence, 200, 50) == 262);
        Assert(store.RecordFencedUsage(job.Id, fence, 20, 10, usageComplete: true) == 232);
        var reported = store.Read();
        Assert(reported.Jobs.Single().TokensIn == 220 && reported.Jobs.Single().TokensOut == 60);
        Assert(reported.Jobs.Single().UsageComplete);
        Assert(reported.BudgetReservations.Single().Consumed == 280);

        try
        {
            store.RecordFencedUsage(job.Id, fence with { Generation = fence.Generation - 1 }, 1, 1);
            throw new Exception("Stale execution updated token accounting");
        }
        catch (LostExecutionLeaseException) { }

        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TRIGGER reject_usage_update
                BEFORE UPDATE OF tokens_in,tokens_out ON jobs
                WHEN NEW.tokens_in>=300
                BEGIN SELECT RAISE(ABORT, 'job debit rejected'); END;
                """;
            cmd.ExecuteNonQuery();
        }
        try
        {
            store.RecordFencedUsage(job.Id, fence, 100, 20);
            throw new Exception("Failed job debit committed reservation consumption");
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        Assert(store.Read().BudgetReservations.Single().Consumed == 280 &&
            store.Job(job.Id).TokensIn == 220,
            "Reservation and job debit must roll back together");

        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TRIGGER reject_usage_update;";
            cmd.ExecuteNonQuery();
        }
        Assert(store.RecordFencedUsage(job.Id, fence, 250, 100) == -118,
            "Overrun observations must be committed and returned to the caller");
        Assert(store.Read().BudgetReservations.Single().Consumed == 630);
        store.Change(s => s.Jobs.Single().LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1));
        try
        {
            store.RecordFencedUsage(job.Id, fence, 1, 1);
            throw new Exception("Expired execution updated budget usage");
        }
        catch (LostExecutionLeaseException) { }
        Assert(store.Read().BudgetReservations.Single().Consumed == 630);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Benchmark targeted token debit against aggregate mutation", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-bench-usage-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "bench" };
        var jobs = Enumerable.Range(0, 75).Select(i => new FactoryJob
            { ProjectId = project.Id, Title = "Fixture " + i }).ToArray();
        store.Change(s => { s.Projects.Add(project); s.Jobs.AddRange(jobs); });
        var job = jobs[0];
        var fence = store.Change(s => JobRecovery.Assign(
            s.Jobs.Single(j => j.Id == job.Id), "bench-runner", 120, local: true));
        const int rounds = 12;
        var direct = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
            store.RecordFencedUsage(job.Id, fence, 1, 1);
        direct.Stop();
        var aggregate = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++)
            store.Change(s =>
            {
                var current = s.Jobs.Single(j => j.Id == job.Id);
                JobRecovery.Require(current, fence);
                current.TokensIn++;
                current.TokensOut++;
            });
        aggregate.Stop();
        Assert(store.Job(job.Id).TokensIn == rounds * 2 &&
            store.Job(job.Id).TokensOut == rounds * 2);
        Console.WriteLine($"BENCHMARK focused token update: {direct.Elapsed.TotalMilliseconds:F2} ms / {rounds}; " +
            $"snapshot update: {aggregate.Elapsed.TotalMilliseconds:F2} ms / {rounds}; " +
            "same database, sequential warm fixture (no performance pass threshold)");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Remote budget grants are controller-authorized, fenced and idempotent", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-remote-budget-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "remote-budget", Origin = "https://example.invalid/repo" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 13000 };
        var jobs = Enumerable.Range(0, 3).Select(_ => new FactoryJob
            { ProjectId = project.Id, GoalId = goal.Id, Mode = JobExecutionModes.Remote }).ToArray();
        store.Change(s =>
        {
            s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.AddRange(jobs);
        });
        var settings = new FactorySettings
        {
            GoalReservationTokens = 4096, GoalExtensionIncrementTokens = 4096,
            MaxJobReservationTokens = 8192
        };
        var fences = new Dictionary<Guid, ExecutionFence>();
        foreach (var job in jobs)
        {
            var worker = "remote-" + job.Id.ToString("N")[..8];
            MissionControlManager.RegisterWorker(store,
                new WorkerInput { Id = worker, Platform = "test" });
            GrantRemoteWorker(store, worker, project.Id);
            fences.Add(job.Id, store.Change(s => JobRecovery.Assign(
                s.Jobs.Single(j => j.Id == job.Id), "remote:" + worker, 120,
                local: false, state: s, reservationTokens: 4096)));
        }

        WorkerBudgetExtensionRequest Request(FactoryJob job, Guid id)
        {
            var fence = fences[job.Id];
            return new WorkerBudgetExtensionRequest
            {
                WorkerId = fence.Owner["remote:".Length..],
                JobId = job.Id, ExecutionGeneration = fence.Generation,
                LeaseToken = fence.Token, RequestId = id, RequestedTokens = 4096
            };
        }
        var requests = jobs.Select(job => Request(job, Guid.NewGuid())).ToArray();
        var grants = requests.Select(req => Task.Run(() =>
            WorkerProtocol.ExtendBudget(new StateStore(home), settings, req))).ToArray();
        Task.WhenAll(grants).GetAwaiter().GetResult();
        Assert(grants.All(task => task.Result is not null));
        Assert(grants.Sum(task => task.Result!.Granted) == 712,
            "Concurrent remote extensions oversubscribed the goal");
        var snapshot = store.Read();
        Assert(GoalBudgetLedger.Liability(snapshot, goal.Id) == 13000);
        Assert(snapshot.BudgetExtensions.Count == 3);
        Assert(snapshot.BudgetExtensions.Sum(e => e.Granted) == 712);
        var repeated = WorkerProtocol.ExtendBudget(store, settings, requests[0]);
        Assert(repeated is not null && repeated.Granted == grants[0].Result!.Granted);
        Assert(store.Read().BudgetExtensions.Count == 3,
            "Repeated request allocated more budget or duplicate history");

        var stale = Request(jobs[0], Guid.NewGuid());
        stale.ExecutionGeneration--;
        Assert(WorkerProtocol.ExtendBudget(store, settings, stale) is null);
        var spoofed = Request(jobs[0], Guid.NewGuid());
        spoofed.WorkerId = "unknown-worker";
        Assert(WorkerProtocol.ExtendBudget(store, settings, spoofed) is null);
        store.Change(s => s.Jobs.Single(j => j.Id == jobs[0].Id).LeaseUntil =
            DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert(WorkerProtocol.ExtendBudget(store, settings, requests[0]) is null,
            "Expired remote lease replayed a prior grant");
        Assert(!WorkerAuthentication.IsWorkerEndpoint("GET", "/api/worker/budget/extend"));
        Assert(WorkerAuthentication.IsWorkerEndpoint("POST", "/api/worker/budget/extend"));
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Schema 5 budget state upgrades without data loss", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-budget-schema-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "migration" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 15000 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job); });
        store.Change(s => JobRecovery.Assign(s.Jobs.Single(), "migration-worker", 120,
            local: true, state: s, reservationTokens: 4096));
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                DROP TABLE goal_budget_extensions;
                PRAGMA user_version=5;
                """;
            command.ExecuteNonQuery();
        }
        var upgraded = new StateStore(home);
        var state = upgraded.Read();
        Assert(state.Projects.Single().Id == project.Id && state.Goals.Single().Id == goal.Id);
        Assert(state.BudgetReservations.Single().Reserved == 4096,
            "Schema upgrade lost the active reservation");
        upgraded.Change(s => GoalBudgetLedger.ExtendLocal(s, s.Jobs.Single(), 1024, 8192));
        Assert(upgraded.Read().BudgetExtensions.Single().Granted == 1024,
            "Migrated database cannot persist extension history");
        using var verify = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}");
        verify.Open();
        using var query = verify.CreateCommand();
        query.CommandText = "PRAGMA user_version;";
        Assert(Convert.ToInt64(query.ExecuteScalar()) == 18);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("App-server token usage counts deltas on resumed threads", () =>
{
    var tracker = new AppServerTokenTracker();
    var samples = new[]
    {
        """{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"total":{"inputTokens":1120,"outputTokens":540},"last":{"inputTokens":120,"outputTokens":40}}}}""",
        """{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"total":{"inputTokens":1200,"outputTokens":570},"last":{"inputTokens":80,"outputTokens":30}}}}""",
        """{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"total":{"inputTokens":1200,"outputTokens":570},"last":{"inputTokens":80,"outputTokens":30}}}}"""
    };
    foreach (var sample in samples)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(sample);
        Assert(tracker.Observe(doc.RootElement));
    }
    Assert(tracker.Reported);
    Assert(tracker.Turn.InputTokens == 200 && tracker.Turn.OutputTokens == 70,
        "Previous thread usage was double-counted");
    using var missing = System.Text.Json.JsonDocument.Parse(
        """{"method":"thread/tokenUsage/updated","params":{"tokenUsage":{"total":{"inputTokens":5000,"outputTokens":20}}}}""");
    Assert(!new AppServerTokenTracker().Observe(missing.RootElement),
        "Unbounded totals without an initial delta must not be charged");
});

Test("Same-goal disjoint packages schedule concurrently within reserved capacity", () =>
{
    var project = new ProjectInfo { MaxParallel = 5 };
    var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 8192 };
    var active = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id,
        State = JobStates.Queued, ExpectedPaths = ["src/a.cs"] };
    var queued = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id,
        ExpectedPaths = ["docs/b.md"] };
    var third = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id,
        ExpectedPaths = ["src/c.cs"] };
    var state = new FactoryState { Projects = [project], Goals = [goal],
        Jobs = [active, queued, third] };
    var chosen = JobScheduler.Select(state, 5, reservationTokens: 4096);
    Assert(chosen.Count == 2, "Scheduler oversubscribed three 4096-token reservations");
    Assert(chosen.Select(j => j.Id).SequenceEqual([active.Id, queued.Id]));
    goal.TokenBudget = 13000;
    Assert(JobScheduler.Select(state, 5, reservationTokens: 4096).Count == 3,
        "Disjoint same-goal packages were unnecessarily serialized");
    queued.ExpectedPaths = ["src/a.cs"];
    Assert(JobScheduler.Select(state, 5, reservationTokens: 4096)
        .All(j => j.Id != queued.Id), "Overlapping work packages were scheduled concurrently");
});

await TestAsync("Budgeted runtime checks before every invocation and records reviews", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-budget-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "budget-test" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 512 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job); });
        var scripted = new UsageTestRuntime();
        scripted.Reports.Enqueue(new AgentTokenUsage(80, 20));
        scripted.Reports.Enqueue(new AgentTokenUsage(75, 20));
        scripted.Reports.Enqueue(new AgentTokenUsage(80, 30));
        var fence = store.Change(s =>
            JobRecovery.Assign(s.Jobs.Single(j => j.Id == job.Id), "budget-test", 120,
                local: true, state: s));
        var runtime = new BudgetedAgentRuntime(scripted, store, job.Id, fence);
        await runtime.RunAsync(home, "builder", "", CancellationToken.None);
        await runtime.RunAsync(home, "review", "", CancellationToken.None, readOnly: true);
        await runtime.RunAsync(home, "rework", "", CancellationToken.None);
        var recorded = store.Job(job.Id);
        Assert(recorded.TokensIn == 235 && recorded.TokensOut == 70,
            "Builder/reviewer usage not aggregated");
        try
        {
            await runtime.RunAsync(home, "fourth", "", CancellationToken.None);
            throw new Exception("Low budget allowed a fourth turn");
        }
        catch (TokenBudgetExceededException) { }
        Assert(scripted.Calls == 3, "Provider was called despite insufficient budget");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Three concurrent goal claims never oversubscribe dynamic extensions", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-extensions-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "extension-concurrency" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 13000 };
        var jobs = Enumerable.Range(0, 3).Select(_ => new FactoryJob
            { ProjectId = project.Id, GoalId = goal.Id }).ToArray();
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.AddRange(jobs); });
        foreach (var job in jobs)
            store.Change(s => JobRecovery.Assign(s.Jobs.Single(j => j.Id == job.Id),
                "local-" + job.Id, 120, local: true, state: s, reservationTokens: 4096));
        var granted = jobs.Select(job => Task.Run(() => new StateStore(home).Change(s =>
            GoalBudgetLedger.ExtendLocal(s, s.Jobs.Single(j => j.Id == job.Id),
                increment: 4096, perJobLimit: 8192)))).ToArray();
        Task.WhenAll(granted).GetAwaiter().GetResult();
        var snapshot = new StateStore(home).Read();
        Assert(granted.Sum(task => task.Result) == 712, "Extensions did not allocate remaining capacity exactly once");
        Assert(GoalBudgetLedger.Liability(snapshot, goal.Id) == 13000,
            "Concurrent extensions oversubscribed the goal");
        Assert(snapshot.BudgetReservations.Count(r => r.State == GoalBudgetLedger.Active) == 3);
        Assert(snapshot.Events.Count(e => e.Type == "budget_extended") == 1);
        Assert(snapshot.BudgetExtensions.Count == 1 &&
            snapshot.BudgetExtensions.Single().Granted == 712,
            "Extension grants must survive as durable history records");
        using (var details = System.Text.Json.JsonDocument.Parse(
            Json.Stringify(GoalBudgetLedger.Summary(snapshot, goal.Id))))
        {
            var budget = details.RootElement;
            Assert(budget.GetProperty("activeOutstanding").GetInt64() == 13000);
            Assert(budget.GetProperty("heldOutstanding").GetInt64() == 0);
            Assert(!budget.GetProperty("requiresReconciliation").GetBoolean());
            Assert(budget.GetProperty("jobs").GetArrayLength() == 3);
            Assert(budget.GetProperty("extensions").GetArrayLength() == 1);
        }
        var winner = snapshot.Jobs.Single(j => snapshot.BudgetReservations.Any(r =>
            r.JobId == j.Id && r.Reserved > 4096));
        store.Change(s =>
        {
            var live = s.Jobs.Single(j => j.Id == winner.Id);
            live.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
        });
        try
        {
            store.Change(s => GoalBudgetLedger.ExtendLocal(s,
                s.Jobs.Single(j => j.Id == winner.Id), 4096, 8192));
            throw new Exception("Expired worker extended its budget");
        }
        catch (LostExecutionLeaseException) { }
        Assert(new StateStore(home).Read().BudgetReservations.Sum(r => r.Reserved) == 13000);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Budgeted runtime extends a live local reservation before exhaustion", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-runtime-extension-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "runtime-extension" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 1500 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job); });
        var fence = store.Change(s => JobRecovery.Assign(s.Jobs.Single(), "local-worker", 120,
            local: true, state: s, reservationTokens: 512));
        var scripted = new UsageTestRuntime();
        scripted.Reports.Enqueue(new AgentTokenUsage(350, 50));
        var runtime = new BudgetedAgentRuntime(scripted, store, job.Id, fence,
            new GoalBudgetExtensionPolicy(512, 256, 1024));
        await runtime.RunAsync(home, "first turn", "", CancellationToken.None);
        var snapshot = store.Read();
        var reservation = snapshot.BudgetReservations.Single();
        Assert(reservation.Reserved == 1024 && reservation.Consumed == 400);
        Assert(snapshot.Events.Any(e => e.Type == "budget_extended"));
        Assert(GoalBudgetLedger.Available(snapshot, goal.Id) == 476,
            "Unused goal capacity was incorrectly debited by reservation extension");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Budgeted runtime records failure usage and interrupts exhausted turns", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-budget-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "budget-failure" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 400 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job); });
        var scripted = new UsageTestRuntime { ReportFailure = true };
        scripted.Reports.Enqueue(new AgentTokenUsage(50, 20));
        scripted.Reports.Enqueue(new AgentTokenUsage(270, 100));
        var fence = store.Change(s =>
            JobRecovery.Assign(s.Jobs.Single(j => j.Id == job.Id), "budget-failure", 120,
                local: true, state: s));
        var runtime = new BudgetedAgentRuntime(scripted, store, job.Id, fence);
        var failed = await runtime.RunAsync(home, "fail", "", CancellationToken.None);
        Assert(!failed.Success);
        Assert(store.Job(job.Id).TokensIn == 50 && store.Job(job.Id).TokensOut == 20);
        try
        {
            await runtime.RunAsync(home, "overrun", "", CancellationToken.None);
            throw new Exception("Over-budget turn was allowed to finish");
        }
        catch (TokenBudgetExceededException) { }
        var current = store.Job(job.Id);
        Assert(current.TokensIn == 320 && current.TokensOut == 120,
            "Usage before cancellation must remain persisted");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Goal budget fails closed when usage is unavailable", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-budget-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "missing-usage" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 1000 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job); });
        var fence = store.Change(s =>
            JobRecovery.Assign(s.Jobs.Single(j => j.Id == job.Id), "budget-test", 120,
                local: true, state: s));
        var runtime = new BudgetedAgentRuntime(new UsageTestRuntime(), store, job.Id, fence);
        try
        {
            await runtime.RunAsync(home, "no usage", "", CancellationToken.None);
            throw new Exception("Missing usage was silently accepted");
        }
        catch (TokenBudgetExceededException ex)
        {
            Assert(ex.Message.Contains("did not report"));
        }
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Remote goal claims retain remaining budget and failed usage", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-remote-budget-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "remote", Origin = "https://example.invalid/repo" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 1000 };
        var earlier = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, State = JobStates.Integrated,
            Integrated = true, TokensIn = 200, TokensOut = 50 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, Mode = "remote", TokensIn = 20 };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(earlier); s.Jobs.Add(job);
            s.Workers.Add(new WorkerInfo { Id = "worker-01" });
        });
        GrantRemoteWorker(store, "worker-01", project.Id);
        var claim = WorkerProtocol.Claim(store, new FactorySettings { AllowPush = true }, "worker-01");
        Assert(claim?.Goal is not null && claim.Goal.TokenBudget == 750);
        Assert(claim!.Job.TokensIn == 20);
        Assert(WorkerProtocol.Heartbeat(store, new WorkerStatus
        {
            WorkerId = "worker-01", JobId = job.Id, TokensIn = 70, TokensOut = 15,
            ExecutionGeneration = claim.Job.ExecutionGeneration, LeaseToken = claim.Job.LeaseToken
        }, new FactorySettings()));
        Assert(store.Job(job.Id).TokensIn == 70 && store.Job(job.Id).TokensOut == 15);
        Assert(await WorkerProtocol.CompleteAsync(store, new WorkerStatus
        {
            WorkerId = "worker-01", JobId = job.Id,
            ExecutionGeneration = claim.Job.ExecutionGeneration, LeaseToken = claim.Job.LeaseToken,
            Job = new FactoryJob { State = JobStates.Blocked, TokensIn = 95, TokensOut = 33 }
        }, new RefusingWorkerVerifier()));
        Assert(store.Job(job.Id).TokensIn == 95 && store.Job(job.Id).TokensOut == 33);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Controller rejects forged GitHub PR identity and head metadata", () =>
{
    var repo = GitHubWorkerResultVerifier.GitHubRepository("https://github.com/Owner/Repository.git");
    Assert(repo == "Owner/Repository");
    Assert(GitHubWorkerResultVerifier.PullNumber(
        "https://github.com/owner/repository/pull/42", repo) == 42);
    void Reject(Action action)
    {
        try { action(); throw new Exception("Forged GitHub identity was accepted"); }
        catch (InvalidOperationException) { }
    }
    Reject(() => GitHubWorkerResultVerifier.PullNumber(
        "https://github.com/other/repository/pull/42", repo));
    Reject(() => GitHubWorkerResultVerifier.PullNumber(
        "https://github.com/owner/repository/pull/42?unexpected=1", repo));
    Reject(() => GitHubWorkerResultVerifier.GitHubRepository("file:///tmp/malicious.git"));

    var expectedSha = new string('a', 40);
    var branch = "factory/12345678901234567890123456789012";
    string Payload(string baseRepo, string headRepo, string baseBranch, string headBranch, string sha)
        => Json.Stringify(new
        {
            state = "open",
            @base = new { repo = new { full_name = baseRepo }, @ref = baseBranch },
            head = new { repo = new { full_name = headRepo }, @ref = headBranch, sha }
        });
    void Validate(string body)
    {
        using var json = System.Text.Json.JsonDocument.Parse(body);
        GitHubWorkerResultVerifier.ValidatePull(json, repo, "master", branch, expectedSha);
    }
    Validate(Payload(repo, repo, "master", branch, expectedSha));
    Reject(() => Validate(Payload(repo, "Other/Repository", "master", branch, expectedSha)));
    Reject(() => Validate(Payload("Other/Repository", repo, "master", branch, expectedSha)));
    Reject(() => Validate(Payload(repo, repo, "main", branch, expectedSha)));
    Reject(() => Validate(Payload(repo, repo, "master", "factory/other", expectedSha)));
    Reject(() => Validate(Payload(repo, repo, "master", branch, new string('b', 40))));
});

await TestAsync("Worker evidence must match job, SHA, attempts, checks and reviews", async () =>
{
    var project = new ProjectInfo { Checks = [new CheckCommand { Name = "build", Executable = "dotnet" }] };
    var job = new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Remote, Attempt = 2 };
    var sha = new string('a', 40);
    var result = new FactoryJob { Id = job.Id, ProjectId = job.ProjectId,
        Mode = JobExecutionModes.Remote, Attempt = job.Attempt,
        State = JobStates.PrReady, HeadSha = sha, Branch = $"factory/{job.Id:N}" };
    var report = new QualityResult { Passed = true, HeadSha = sha,
        Checks = [new CheckReport { Name = "build", ExitCode = 0 }],
        Reviews = new Dictionary<string, string>
        {
            ["code"] = "PASS\nOK", ["architecture"] = "PASS\nOK",
            ["security"] = "PASS\nOK", ["ux"] = "PASS\nOK"
        } };
    string Evidence(QualityResult q, string commit, int attempt) => Json.Stringify(new
    {
        jobId = job.Id, commit, report = q, attempt, outcome = "ready"
    });
    var status = new WorkerStatus { WorkerId = "worker-01", JobId = job.Id, Job = result,
        Evidence = Evidence(report, sha, job.Attempt) };
    _ = GitHubWorkerResultVerifier.ValidateEvidence(project, job, status);
    void Reject(string reason)
    {
        try { _ = GitHubWorkerResultVerifier.ValidateEvidence(project, job, status);
            throw new Exception("Invalid worker evidence accepted: " + reason); }
        catch (InvalidOperationException) { }
    }
    status.Evidence = Evidence(report, new string('b', 40), job.Attempt);
    Reject("different commit");
    status.Evidence = Evidence(report, sha, job.Attempt + 1);
    Reject("different attempt");
    report.Checks[0].ExitCode = 1; status.Evidence = Evidence(report, sha, job.Attempt);
    Reject("failed check");
    report.Checks[0].ExitCode = 0; report.Checks[0].Name = "unexpected";
    status.Evidence = Evidence(report, sha, job.Attempt);
    Reject("check identity");
    report.Checks[0].Name = "build"; report.Reviews.Remove("security");
    status.Evidence = Evidence(report, sha, job.Attempt);
    Reject("missing review");
    await Task.CompletedTask;
});

await TestAsync("Unverified remote success cannot mark a job PR-ready", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-verify-reject-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "remote", Origin = "https://github.com/example/repo" };
        var job = new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Remote };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Jobs.Add(job);
            s.Workers.Add(new WorkerInfo { Id = "worker-01" });
        });
        GrantRemoteWorker(store, "worker-01", project.Id);
        var claim = WorkerProtocol.Claim(store, new FactorySettings { AllowPush = true }, "worker-01");
        Assert(claim != null);
        var finished = new FactoryJob { Id = job.Id, ProjectId = project.Id, Attempt = claim!.Job.Attempt,
            Mode = JobExecutionModes.Remote, State = JobStates.PrReady, HeadSha = new string('a', 40),
            Branch = $"factory/{job.Id:N}", PrUrl = "https://github.com/example/repo/pull/1" };
        var status = new WorkerStatus { WorkerId = "worker-01", JobId = job.Id, Job = finished,
            Evidence = "{\"forged\":true}", TokensIn = 10,
            ExecutionGeneration = claim.Job.ExecutionGeneration, LeaseToken = claim.Job.LeaseToken };
        Assert(await WorkerProtocol.CompleteAsync(store, status, new RefusingWorkerVerifier()));
        var blocked = store.Job(job.Id);
        Assert(blocked.State == JobStates.Blocked && blocked.Stage == "remote_verification_failed");
        Assert(string.IsNullOrEmpty(blocked.PrUrl) && string.IsNullOrEmpty(blocked.HeadSha) &&
            string.IsNullOrEmpty(blocked.EvidenceFile), "Unverified worker metadata was persisted");
        Assert(blocked.TokensIn == 10, "Remote usage accounting was dropped");
        Assert(!await WorkerProtocol.CompleteAsync(store, status, new RefusingWorkerVerifier()),
            "A stale worker completion was replayed");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Controller-verified remote results are atomically bound to the active lease", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-verify-accept-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "remote", Origin = "https://github.com/example/repo" };
        var job = new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Remote };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Jobs.Add(job);
            s.Workers.Add(new WorkerInfo { Id = "worker-01" });
        });
        GrantRemoteWorker(store, "worker-01", project.Id);
        var claim = WorkerProtocol.Claim(store, new FactorySettings { AllowPush = true }, "worker-01");
        Assert(claim != null);
        var finished = new FactoryJob { Id = job.Id, ProjectId = project.Id, Attempt = claim!.Job.Attempt,
            Mode = JobExecutionModes.Remote, State = JobStates.PrReady, HeadSha = new string('a', 40),
            Branch = $"factory/{job.Id:N}", PrUrl = "https://github.com/example/repo/pull/1" };
        var status = new WorkerStatus { WorkerId = "worker-01", JobId = job.Id, Job = finished,
            ExecutionGeneration = claim.Job.ExecutionGeneration, LeaseToken = claim.Job.LeaseToken };
        Assert(await WorkerProtocol.CompleteAsync(store, status,
            new FixedWorkerVerifier(new VerifiedWorkerResult("{\"verifiedBy\":\"test\"}"))));
        var ready = store.Job(job.Id);
        Assert(ready.State == JobStates.PrReady && ready.HeadSha == finished.HeadSha &&
            ready.PrUrl == finished.PrUrl && File.Exists(ready.EvidenceFile));
        Assert(File.ReadAllText(ready.EvidenceFile).Contains("verifiedBy"));
        Assert(!await WorkerProtocol.CompleteAsync(store, status,
            new FixedWorkerVerifier(new VerifiedWorkerResult("{}"))));
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});


Test("SQLite 4 upgrade preserves workers without granting remote access", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-worker-upgrade-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "existing", Origin = "https://example.invalid/repo" };
        var job = new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Remote };
        store.Change(state =>
        {
            state.Projects.Add(project);
            state.Jobs.Add(job);
            state.Workers.Add(new WorkerInfo { Id = "legacy-worker" });
        });

        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                DROP TABLE worker_project_grants;
                DROP TABLE worker_capability_proofs;
                DROP TABLE projects_required_execution_capabilities;
                DROP TABLE jobs_required_execution_capabilities;
                PRAGMA user_version=4;
                """;
            cmd.ExecuteNonQuery();
        }

        var migrated = new StateStore(home);
        Assert(migrated.Read().Workers.Single().Id == "legacy-worker");
        Assert(migrated.Read().WorkerProjectGrants.Count == 0 &&
            migrated.Read().WorkerCapabilityProofs.Count == 0);
        Assert(WorkerProtocol.Claim(migrated, new FactorySettings { AllowPush = true },
            "legacy-worker") is null, "Migration granted privileges to an existing worker");
        GrantRemoteWorker(migrated, "legacy-worker", project.Id);
        Assert(WorkerProtocol.Claim(migrated, new FactorySettings { AllowPush = true },
            "legacy-worker")?.Job.Id == job.Id);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("SQLite 2 state migrates fencing columns without losing jobs", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-recovery-migrate-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "prior-schema" };
        var job = new FactoryJob { ProjectId = project.Id, Title = "Retained" };
        store.Change(s => { s.Projects.Add(project); s.Jobs.Add(job); });
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                DROP TABLE worker_project_grants;
                DROP TABLE worker_capability_proofs;
                DROP TABLE projects_required_execution_capabilities;
                DROP TABLE jobs_required_execution_capabilities;
                DROP TABLE goal_budget_reservations;
                ALTER TABLE jobs DROP COLUMN usage_complete;
                ALTER TABLE jobs DROP COLUMN execution_generation;
                ALTER TABLE jobs DROP COLUMN lease_token;
                ALTER TABLE jobs DROP COLUMN worker_process_id;
                ALTER TABLE jobs DROP COLUMN worker_machine_name;
                ALTER TABLE jobs DROP COLUMN worker_process_started_at;
                ALTER TABLE jobs DROP COLUMN checkpoint_stage;
                ALTER TABLE jobs DROP COLUMN checkpoint_commit;
                ALTER TABLE jobs DROP COLUMN checkpoint_at;
                PRAGMA user_version=2;
                """;
            cmd.ExecuteNonQuery();
        }
        var migrated = new StateStore(home);
        Assert(migrated.Job(job.Id).Title == "Retained");
        Assert(migrated.Job(job.Id).ExecutionGeneration == 0);
        using var check = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "PRAGMA user_version;";
        Assert(Convert.ToInt64(query.ExecuteScalar()) == 18);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Expired local lease is quarantined and cannot be replayed", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-fence-quarantine-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "recovery" };
        var job = new FactoryJob { ProjectId = project.Id, ExpectedPaths = ["src/a.cs"] };
        store.Change(s => { s.Projects.Add(project); s.Jobs.Add(job); });
        ExecutionFence original = default;
        store.Change(s =>
        {
            var owned = s.Jobs.Single();
            original = JobRecovery.Assign(owned, "old-local-runner", 120, local: true);
            JobRecovery.Checkpoint(owned, "agent", "0000000000000000000000000000000000000000");
            owned.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
        });
        var generation = store.Job(job.Id).ExecutionGeneration;
        var engine = new FactoryEngine(store, new FactorySettings(), agent: new FakeAgent());
        Assert(engine.Recover().SequenceEqual([job.Id]));
        var blocked = new StateStore(home).Job(job.Id);
        Assert(blocked.State == JobStates.Blocked && blocked.Stage == "reconcile");
        Assert(blocked.ExecutionGeneration == generation + 1 &&
            blocked.CheckpointStage == "agent" && blocked.LeaseToken.Length == 0);
        Assert(!JobRecovery.Matches(blocked, original));
        Assert(engine.Recover().Count == 0, "Recovery should be idempotent");
        await engine.RunQueueAsync(CancellationToken.None);
        Assert(store.Job(job.Id).Attempt == 1 && store.Job(job.Id).State == JobStates.Blocked,
            "Queue automatically replayed unverified work");
        try
        {
            MissionControlManager.ControlJob(store, job.Id, new ControlInput { Action = "retry", Reviewed = true });
            throw new Exception("Unreconciled job was requeued");
        }
        catch (InvalidOperationException) { }
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Reconciliation checks process identity, worktree, and committed checkpoints", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-fence-reconcile-" + Guid.NewGuid().ToString("N"));
    var home = Path.Combine(Path.GetTempPath(), "factory-fence-state-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    void Git(params string[] args) =>
        Commands.Require(Commands.Run("git", ["-C", root, ..args], root), "git fixture");
    try
    {
        Git("init", "-b", "master");
        Git("config", "user.name", "Factory Tests");
        Git("config", "user.email", "factory-tests@example.invalid");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        Git("add", "README.md"); Git("commit", "-m", "Initial");
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "recovery", Path = root };
        var job = new FactoryJob { ProjectId = project.Id, ExpectedPaths = ["README.md"] };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Jobs.Add(job);
            var owned = s.Jobs.Single();
            JobRecovery.Assign(owned, "owner", 120, local: true);
            owned.Worktree = root;
            JobRecovery.Checkpoint(owned, "agent", GitOperations.Head(root));
            owned.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
        });
        Assert(JobRecovery.QuarantineExpired(store).SequenceEqual([job.Id]));
        void Deny(Action action)
        {
            try { action(); throw new Exception("Unsafe recovery was accepted"); }
            catch (InvalidOperationException) { }
        }
        Deny(() => JobRecovery.ReconcileLocal(store, job.Id, false, "Reviewed repository"));
        var actualHost = store.Job(job.Id).WorkerMachineName;
        store.UpdateJob(job.Id, j => j.WorkerMachineName = "unrecognized-remote-host");
        Deny(() => JobRecovery.ReconcileLocal(store, job.Id, true, "Reviewed previous process"));
        store.UpdateJob(job.Id, j => j.WorkerMachineName = actualHost);
        using (var stillRunning = new CancellationTokenSource())
        {
            LocalJobControl.Register(job.Id, stillRunning);
            Deny(() => JobRecovery.ReconcileLocal(store, job.Id, true, "Reviewed previous process"));
            LocalJobControl.Unregister(job.Id);
        }
        File.AppendAllText(Path.Combine(root, "README.md"), "dirty");
        Deny(() => JobRecovery.ReconcileLocal(store, job.Id, true, "Reviewed uncommitted change"));
        Git("checkout", "--", "README.md");
        var before = store.Job(job.Id).ExecutionGeneration;
        var reconciled = JobRecovery.ReconcileLocal(store, job.Id, true, "Verified process ended and worktree clean");
        Assert(reconciled.State == JobStates.Queued && reconciled.Stage == "reconciled");
        Assert(store.Read().Events.Any(e => e.JobId == job.Id && e.Type == "reconciled"));
        ExecutionFence next = default;
        store.Change(s => next = JobRecovery.Assign(s.Jobs.Single(), "new-owner", 120, local: true));
        Assert(next.Generation > before && !JobRecovery.Matches(store.Job(job.Id),
            new ExecutionFence(before, "", "owner")), "Old owner regained control");
        Assert(next.Token.Length > 0 && next.Owner == "new-owner");
    }
    finally
    {
        try { Directory.Delete(root, true); } catch { }
        try { Directory.Delete(home, true); } catch { }
    }
});

await TestAsync("Remote worker generation fences stale heartbeats and completions", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-fence-remote-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "remote", Origin = "https://github.com/example/repo" };
        var job = new FactoryJob { ProjectId = project.Id, Mode = JobExecutionModes.Remote,
            ExpectedPaths = ["src/a.cs"] };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Jobs.Add(job);
            s.Workers.Add(new WorkerInfo { Id = "worker-01" });
        });
        var settings = new FactorySettings { AllowPush = true };
        GrantRemoteWorker(store, "worker-01", project.Id);
        var first = WorkerProtocol.Claim(store, settings, "worker-01");
        Assert(first is not null);
        var stale = new WorkerStatus { WorkerId = "worker-01", JobId = job.Id,
            ExecutionGeneration = first!.Job.ExecutionGeneration, LeaseToken = first.Job.LeaseToken };
        Assert(WorkerProtocol.Heartbeat(store, stale, settings));
        store.UpdateJob(job.Id, j => j.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert(JobRecovery.QuarantineExpired(store, JobExecutionModes.Remote).Count == 1);
        Assert(!WorkerProtocol.Heartbeat(store, stale, settings));
        Assert(!await WorkerProtocol.CompleteAsync(store, stale, new RefusingWorkerVerifier()));
        store.UpdateJob(job.Id, j => { j.State = JobStates.Queued; j.Stage = "operator_reviewed"; });
        var second = WorkerProtocol.Claim(store, settings, "worker-01");
        Assert(second is not null && second.Job.ExecutionGeneration > stale.ExecutionGeneration);
        Assert(second!.Job.LeaseToken != stale.LeaseToken);
        Assert(!WorkerProtocol.Heartbeat(store, stale, settings));
        Assert(!await WorkerProtocol.CompleteAsync(store, stale, new RefusingWorkerVerifier()));
        var current = new WorkerStatus { WorkerId = "worker-01", JobId = job.Id,
            ExecutionGeneration = second.Job.ExecutionGeneration, LeaseToken = second.Job.LeaseToken };
        Assert(WorkerProtocol.Heartbeat(store, current, settings));
        Assert(await WorkerProtocol.CompleteAsync(store, current, new RefusingWorkerVerifier()));
        Assert(store.Job(job.Id).State == JobStates.Blocked);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});


await TestAsync("Old local runner cannot commit or complete after its generation is fenced", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-fence-live-" + Guid.NewGuid().ToString("N"));
    var repo = Path.Combine(root, "repository");
    Directory.CreateDirectory(repo);
    void Git(params string[] args) =>
        Commands.Require(Commands.Run("git", ["-C", repo, ..args], root), "git fixture");
    try
    {
        Git("init", "-b", "master");
        Git("config", "user.name", "Factory Tests");
        Git("config", "user.email", "factory-tests@example.invalid");
        File.WriteAllText(Path.Combine(repo, "README.md"), "Base");
        Git("add", "README.md"); Git("commit", "-m", "Base");
        var store = new StateStore(Path.Combine(root, "state"));
        var project = new ProjectInfo { Name = "fencing", Path = repo,
            Checks = [new CheckCommand { Name = "git", Executable = "git",
                Arguments = ["rev-parse", "HEAD"] }] };
        var job = new FactoryJob { ProjectId = project.Id, Title = "Fenced",
            ExpectedPaths = ["src/generated.txt"] };
        store.Change(s => { s.Projects.Add(project); s.Jobs.Add(job); });
        var engine = new FactoryEngine(store, new FactorySettings { MaxParallel = 1 },
            agent: new ExpireDuringRunAgent(store, job.Id));
        await engine.RunQueueAsync(CancellationToken.None);
        var blocked = store.Job(job.Id);
        Assert(blocked.State == JobStates.Blocked && blocked.Stage == "reconcile");
        Assert(blocked.HeadSha == "" && blocked.PrUrl == "" && blocked.EvidenceFile == "",
            "Stale execution persisted completion metadata");
        Assert(blocked.Attempt == 1 && blocked.ExecutionGeneration >= 2);
        Assert(store.Read().Events.Count(e => e.JobId == job.Id && e.Type == "reconciliation_required") == 1);
        Assert(GitOperations.Head(blocked.Worktree) == GitOperations.Head(repo),
            "Stale runner committed after losing its fence");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

await TestAsync("Concurrent SQLite claims reserve the same goal budget atomically", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-goal-ledger-" + Guid.NewGuid().ToString("N"));
    try
    {
        var state = new StateStore(home);
        var project = new ProjectInfo { Name = "parallel", MaxParallel = 8 };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 8192 };
        var jobs = Enumerable.Range(0, 4).Select(i => new FactoryJob
        {
            ProjectId = project.Id, GoalId = goal.Id, Title = "Parallel " + i,
            ExpectedPaths = ["src/file" + i + ".cs"]
        }).ToArray();
        state.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.AddRange(jobs); });

        var claimed = await Task.WhenAll(jobs.Select(async job =>
            await Task.Run(() =>
            {
                try
                {
                    return new StateStore(home).Change(s =>
                    {
                        var current = s.Jobs.Single(j => j.Id == job.Id);
                        JobRecovery.Assign(current, "runner-" + job.Id, 120, true, s, 4096);
                        return true;
                    });
                }
                catch (TokenBudgetExceededException) { return false; }
            })));

        Assert(claimed.Count(value => value) == 2, "Concurrent reservations exceeded 8192 tokens");
        var snapshot = new StateStore(home).Read();
        Assert(snapshot.BudgetReservations.Count == 2 &&
            snapshot.BudgetReservations.All(r => r.Reserved == 4096 && r.State == GoalBudgetLedger.Active));
        Assert(GoalBudgetLedger.Available(snapshot, goal.Id) == 0);
        Assert(GoalBudgetLedger.Liability(snapshot, goal.Id) == goal.TokenBudget);
        Assert(snapshot.Jobs.Count(j => j.State == JobStates.Preparing) == 2);
        Assert(snapshot.Jobs.Count(j => j.State == JobStates.Queued) == 2);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Goal ledger debits, releases, holds and reconciles unused allowance", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-goal-release-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "accounting" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 10000 };
        var first = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        var second = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(first); s.Jobs.Add(second); });
        store.Change(s =>
        {
            JobRecovery.Assign(s.Jobs.Single(j => j.Id == first.Id), "a", 120, true, s, 4000);
            JobRecovery.Assign(s.Jobs.Single(j => j.Id == second.Id), "b", 120, true, s, 4000);
        });
        Assert(GoalBudgetLedger.Available(store.Read(), goal.Id) == 2000);
        store.Change(s =>
        {
            var j = s.Jobs.Single(j => j.Id == first.Id);
            Assert(GoalBudgetLedger.Record(s, j, 900, 100) == 3000);
            j.UsageComplete = true;
            j.State = JobStates.CompletedLocal;
            GoalBudgetLedger.Settle(s, j);
            j.LeaseOwner = ""; j.LeaseToken = ""; j.LeaseUntil = null;
        });
        var afterRelease = store.Read();
        Assert(GoalBudgetLedger.Available(afterRelease, goal.Id) == 5000);
        Assert(afterRelease.BudgetReservations.Single(r => r.JobId == first.Id).State ==
            GoalBudgetLedger.Released);
        store.Change(s =>
        {
            var j = s.Jobs.Single(j => j.Id == second.Id);
            Assert(GoalBudgetLedger.Record(s, j, 200, 50, false) == 3750);
            GoalBudgetLedger.Hold(s, j, "incomplete provider usage");
            j.State = JobStates.Blocked;
            j.Stage = "reconcile";
            j.LeaseOwner = ""; j.LeaseToken = ""; j.LeaseUntil = null;
        });
        var uncertain = store.Read();
        Assert(GoalBudgetLedger.Available(uncertain, goal.Id) == 5000);
        Assert(GoalBudgetLedger.Liability(uncertain, goal.Id) == 5000);
        var held = uncertain.BudgetReservations.Single(r => r.JobId == second.Id);
        try
        {
            GoalBudgetLedger.Reconcile(store, goal.Id, held.Id, 200, 50, false, "reviewed final usage");
            throw new Exception("Unreviewed budget reconciliation was accepted");
        }
        catch (InvalidOperationException) { }
        GoalBudgetLedger.Reconcile(store, goal.Id, held.Id, 200, 50, true,
            "Verified usage from provider record");
        var reconciled = store.Read();
        Assert(GoalBudgetLedger.Available(reconciled, goal.Id) == 8750);
        Assert(reconciled.BudgetReservations.Single(r => r.Id == held.Id).State == GoalBudgetLedger.Released);
        Assert(reconciled.Events.Any(e => e.Type == "budget_reconciled"));
        try
        {
            MissionControlManager.UpdateGoal(store, goal.Id, new GoalInput
            {
                ProjectId = project.Id, Objective = "Lower budget", TokenBudget = 100
            });
            throw new Exception("Lowered budget below committed usage");
        }
        catch (InvalidOperationException) { }
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("Expired goal reservation remains held and stale generation cannot debit it", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-goal-expiry-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "expired" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 4096 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job); });
        ExecutionFence old = default;
        store.Change(s =>
        {
            var current = s.Jobs.Single();
            old = JobRecovery.Assign(current, "local", 120, true, s, 4096);
            current.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
        });
        Assert(JobRecovery.QuarantineExpired(store).SequenceEqual([job.Id]));
        var state = store.Read();
        Assert(state.Jobs.Single().State == JobStates.Blocked);
        Assert(state.BudgetReservations.Single().State == GoalBudgetLedger.Held);
        Assert(GoalBudgetLedger.Available(state, goal.Id) == 0);
        Assert(!JobRecovery.Matches(state.Jobs.Single(), old));
        try
        {
            store.Change(s =>
            {
                JobRecovery.Require(s.Jobs.Single(), old);
                GoalBudgetLedger.Record(s, s.Jobs.Single(), 10, 0);
            });
            throw new Exception("Stale runner modified a held reservation");
        }
        catch (LostExecutionLeaseException) { }
        Assert(store.Job(job.Id).TokensIn == 0);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

Test("SQLite 3 migration quarantines unreserved goal runners and holds budget", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-goal-migration-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "upgrade" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 2000 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, TokensIn = 200 };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job);
            var current = s.Jobs.Single();
            current.State = JobStates.Building; current.LeaseOwner = "old-runner";
            current.LeaseToken = "old-lease"; current.ExecutionGeneration = 7;
            current.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
        });
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(home, "factory.sqlite3")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                DROP TABLE worker_project_grants;
                DROP TABLE worker_capability_proofs;
                DROP TABLE projects_required_execution_capabilities;
                DROP TABLE jobs_required_execution_capabilities;
                DROP TABLE goal_budget_reservations;
                ALTER TABLE jobs DROP COLUMN usage_complete;
                PRAGMA user_version=3;
                """;
            cmd.ExecuteNonQuery();
        }
        var migrated = new StateStore(home);
        var state = migrated.Read();
        Assert(state.Jobs.Single().ExecutionGeneration == 8 &&
            state.Jobs.Single().State == JobStates.Blocked && state.Jobs.Single().Stage == "reconcile");
        Assert(state.BudgetReservations.Single().State == GoalBudgetLedger.Held);
        Assert(state.BudgetReservations.Single().Reserved == 1800);
        Assert(GoalBudgetLedger.Available(state, goal.Id) == 0);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Disjoint same-goal jobs execute concurrently end to end", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), "factory-parallel-goal-" + Guid.NewGuid().ToString("N"));
    var repo = Path.Combine(root, "repository");
    Directory.CreateDirectory(repo);
    void Git(params string[] args) =>
        Commands.Require(Commands.Run("git", ["-C", repo, ..args], root), "git fixture");
    try
    {
        Git("init", "-b", "master");
        Git("config", "user.email", "factory-tests@example.invalid");
        Git("config", "user.name", "Factory Tests");
        File.WriteAllText(Path.Combine(repo, "README.md"), "Initial");
        Git("add", "README.md"); Git("commit", "-m", "Initial");

        var store = new StateStore(Path.Combine(root, "state"));
        var project = new ProjectInfo { Name = "parallel", Path = repo, MaxParallel = 2,
            Checks = [new CheckCommand { Name = "git", Executable = "git",
                Arguments = ["rev-parse", "HEAD"] }] };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 20000 };
        var a = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, Title = "Build A",
            Description = "Write file A", ExpectedPaths = ["src/a.txt"] };
        var b = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, Title = "Build B",
            Description = "Write file B", ExpectedPaths = ["src/b.txt"] };
        store.Change(s => { s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(a); s.Jobs.Add(b); });
        var agent = new ParallelGoalTestRuntime();
        var engine = new FactoryEngine(store, new FactorySettings
        {
            MaxParallel = 2, GoalReservationTokens = 8192
        }, agent: agent);
        await engine.RunQueueAsync(CancellationToken.None);
        Assert(agent.MaximumConcurrentBuilders == 2,
            "Independent work packages were not actually running concurrently");
        Assert(store.Job(a.Id).State == JobStates.CompletedLocal, store.Job(a.Id).Error);
        Assert(store.Job(b.Id).State == JobStates.CompletedLocal, store.Job(b.Id).Error);
        var snapshot = store.Read();
        Assert(snapshot.BudgetReservations.Count == 2 &&
            snapshot.BudgetReservations.All(r => r.State == GoalBudgetLedger.Released));
        Assert(GoalBudgetLedger.Available(snapshot, goal.Id) > 15000,
            "Completed jobs did not release unused goal reservations");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
});

Test("Remote workers can concurrently claim reserved shares of one goal", () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-remote-parallel-budget-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "remote", MaxParallel = 2,
            Origin = "https://example.invalid/repo" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 18000 };
        var a = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id,
            Mode = JobExecutionModes.Remote, ExpectedPaths = ["src/a.cs"] };
        var b = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id,
            Mode = JobExecutionModes.Remote, ExpectedPaths = ["src/b.cs"] };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(a); s.Jobs.Add(b);
            s.Workers.Add(new WorkerInfo { Id = "worker-a" });
            s.Workers.Add(new WorkerInfo { Id = "worker-b" });
        });
        var settings = new FactorySettings { AllowPush = true, MaxParallel = 2,
            GoalReservationTokens = 8000 };
        GrantRemoteWorker(store, "worker-a", project.Id);
        GrantRemoteWorker(store, "worker-b", project.Id);
        var first = WorkerProtocol.Claim(store, settings, "worker-a");
        var second = WorkerProtocol.Claim(store, settings, "worker-b");
        Assert(first is not null && second is not null && first.Job.Id != second.Job.Id,
            "Second remote worker was blocked by same-goal serialization");
        var state = store.Read();
        Assert(state.BudgetReservations.Count == 2 &&
            state.BudgetReservations.Sum(r => r.Reserved) == 16000 &&
            GoalBudgetLedger.Available(state, goal.Id) == 2000);
        Assert(first!.Goal?.TokenBudget == 8000 && second!.Goal?.TokenBudget == 8000,
            "Worker received unrestricted global budget rather than its reserved share");
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Unverified remote final usage holds budget until reviewed reconciliation", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-remote-unknown-budget-" +
        Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "unverified", Origin = "https://github.com/example/repo" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 1000 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id,
            Mode = JobExecutionModes.Remote, ExpectedPaths = ["src/a.cs"] };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job);
            s.Workers.Add(new WorkerInfo { Id = "worker-01" });
        });
        var settings = new FactorySettings { AllowPush = true };
        GrantRemoteWorker(store, "worker-01", project.Id);
        var claim = WorkerProtocol.Claim(store, settings, "worker-01");
        Assert(claim is not null);
        var claimed = claim!.Job;
        var finished = new FactoryJob { Id = job.Id, ProjectId = project.Id,
            GoalId = goal.Id, Attempt = claimed.Attempt, Mode = JobExecutionModes.Remote,
            State = JobStates.PrReady, HeadSha = new string('a', 40),
            Branch = $"factory/{job.Id:N}", PrUrl = "https://github.com/example/repo/pull/42",
            TokensIn = 100, TokensOut = 20 };
        var report = new WorkerStatus { WorkerId = "worker-01", JobId = job.Id, Job = finished,
            TokensIn = 100, TokensOut = 20, UsageComplete = false,
            ExecutionGeneration = claimed.ExecutionGeneration, LeaseToken = claimed.LeaseToken };
        Assert(await WorkerProtocol.CompleteAsync(store, report,
            new FixedWorkerVerifier(new VerifiedWorkerResult("{\"verifiedBy\":\"test\"}"))));
        var result = store.Job(job.Id);
        Assert(result.State == JobStates.Blocked && result.Stage == "remote_verification_failed");
        var held = store.Read().BudgetReservations.Single();
        Assert(held.State == GoalBudgetLedger.Held && held.Consumed == 120 &&
            GoalBudgetLedger.Available(store.Read(), goal.Id) == 0);
        Assert(!await WorkerProtocol.CompleteAsync(store, report,
            new FixedWorkerVerifier(new VerifiedWorkerResult("{}"))),
            "Replayed completion settled a stale reservation");

        MissionControlManager.ControlJob(store, job.Id,
            new ControlInput { Action = "retry", Reviewed = true });
        Assert(GoalBudgetLedger.Allocation(store.Read(), store.Job(job.Id), 1000) == 0,
            "Job retried before uncertain budget was reconciled");
        GoalBudgetLedger.Reconcile(store, goal.Id, held.Id, 100, 20, true,
            "Verified final provider usage record");
        Assert(GoalBudgetLedger.Allocation(store.Read(), store.Job(job.Id), 1000) == 880);
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

await TestAsync("Remote reservation overrun fences worker and preserves actual usage", async () =>
{
    var home = Path.Combine(Path.GetTempPath(), "factory-goal-overrun-" + Guid.NewGuid().ToString("N"));
    try
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "remote", Origin = "https://example.invalid/repo" };
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 600 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id,
            Mode = JobExecutionModes.Remote, ExpectedPaths = ["src/a.cs"] };
        store.Change(s =>
        {
            s.Projects.Add(project); s.Goals.Add(goal); s.Jobs.Add(job);
            s.Workers.Add(new WorkerInfo { Id = "worker-01" });
        });
        var settings = new FactorySettings { AllowPush = true };
        GrantRemoteWorker(store, "worker-01", project.Id);
        var claim = WorkerProtocol.Claim(store, settings, "worker-01");
        Assert(claim is not null);
        var status = new WorkerStatus { WorkerId = "worker-01", JobId = job.Id,
            ExecutionGeneration = claim!.Job.ExecutionGeneration, LeaseToken = claim.Job.LeaseToken,
            TokensIn = 625, TokensOut = 25 };
        Assert(!WorkerProtocol.Heartbeat(store, status, settings),
            "Over-budget heartbeat should revoke the worker lease");
        var current = store.Job(job.Id);
        Assert(current.State == JobStates.Blocked && current.Stage == "reconcile");
        Assert(current.TokensIn == 625 && current.TokensOut == 25);
        var ledger = store.Read().BudgetReservations.Single();
        Assert(ledger.Reserved == 600 && ledger.Consumed == 650 &&
            ledger.State == GoalBudgetLedger.Held);
        Assert(GoalBudgetLedger.Available(store.Read(), goal.Id) == 0);
        Assert(!await WorkerProtocol.CompleteAsync(store, status, new RefusingWorkerVerifier()),
            "Expired execution generation finalized despite revoked lease");
        try
        {
            GoalBudgetLedger.Reconcile(store, goal.Id, ledger.Id, 625, 25, true,
                "Usage exceeded the reserved allowance");
            throw new Exception("Unbounded usage was silently forgiven");
        }
        catch (InvalidOperationException) { }
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

var passed = results.Count(x => x.Passed);
Console.WriteLine($"{passed}/{results.Count} passed");
return passed == results.Count ? 0 : 1;

internal sealed class ProviderCrashFixture : IAgentRuntime
{
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        File.WriteAllText(Path.Combine(directory, "provider-dispatched"), "");
        await Task.Delay(Timeout.Infinite, cancellation);
        throw new InvalidOperationException("Fixture must be stopped by its parent");
    }
}

internal sealed class FakeAgent : IAgentRuntime
{
    public Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        onSession?.Invoke("fake-session");
        onUsage?.Invoke(new AgentTokenUsage(8, 4));
        if (readOnly) return Task.FromResult(new AgentRunResult { Success = true, FinalText = "VERDICT: PASS\nNo blocking issues", SessionId = "fake-session", InputTokens = 8, OutputTokens = 4, UsageReported = true });
        var src = Path.Combine(directory, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "generated.txt"), "Tested content");
        return Task.FromResult(new AgentRunResult { Success = true, FinalText = "Implementation complete", SessionId = "fake-session", InputTokens = 8, OutputTokens = 4, UsageReported = true });
    }
}

internal sealed class ReviewFixtureAgent(string verdict, bool success = true, bool fallback = false,
    Action? onReview = null) : IAgentRuntime
{
    public List<string> Prompts { get; } = [];
    public Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        if (!readOnly) throw new InvalidOperationException("Fixture requires read-only review");
        Prompts.Add(prompt);
        onReview?.Invoke();
        return Task.FromResult(new AgentRunResult { Success = success, FinalText = verdict, FinalTextIsFallback = fallback });
    }
}

internal sealed class PausedPlanAgent : IAgentRuntime
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        Entered.SetResult();
        await Resume.Task.WaitAsync(cancellation);
        return new AgentRunResult
        {
            Success = true,
            FinalText = """{"packages":[{"title":"Generated","description":"Fixture proposal","files":["README.md"]}]}"""
        };
    }
}

internal sealed class UsageTestRuntime : IAgentRuntime
{
    public readonly Queue<AgentTokenUsage> Reports = new();
    public int Calls { get; private set; }
    public bool ReportFailure { get; set; }

    public Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        Calls++;
        if (Reports.Count == 0)
            return Task.FromResult(new AgentRunResult { Success = true, FinalText = "No usage" });
        var usage = Reports.Dequeue();
        onUsage?.Invoke(usage);
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentRunResult
        {
            Success = !ReportFailure, InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens, UsageReported = true
        });
    }
}

internal sealed class RefusingWorkerVerifier : IWorkerResultVerifier
{
    public Task<VerifiedWorkerResult> VerifyAsync(ProjectInfo project, FactoryJob job,
        WorkerStatus status, CancellationToken ct)
        => throw new InvalidOperationException("Independent GitHub verification failed");
}

internal sealed class FixedWorkerVerifier(VerifiedWorkerResult value) : IWorkerResultVerifier
{
    public Task<VerifiedWorkerResult> VerifyAsync(ProjectInfo project, FactoryJob job,
        WorkerStatus status, CancellationToken ct) => Task.FromResult(value);
}

internal sealed class FixedRemoteBaseReader(string sha) : IRemoteBaseReader
{
    public Task<string> ReadBaseCommitAsync(ProjectInfo project, CancellationToken ct)
        => Task.FromResult(sha);
}

internal sealed class FixedIntegrationPullReader(MergedPullRequest result) : IIntegrationPullRequestReader
{
    public Task<MergedPullRequest> ReadAsync(string repository, int number, CancellationToken ct)
        => Task.FromResult(result);
}

internal sealed class ExpireDuringRunAgent(StateStore store, Guid jobId) : IAgentRuntime
{
    public Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        store.UpdateJob(jobId, j => j.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1));
        JobRecovery.QuarantineExpired(store);
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "src", "generated.txt"), "Uncommitted stale change");
        return Task.FromResult(new AgentRunResult { Success = true });
    }
}

// Builder turns coordinate without blocking the thread pool; reviewers remain read-only.
internal sealed class ParallelGoalTestRuntime : IAgentRuntime
{
    private readonly TaskCompletionSource _bothStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _running;
    private int _started;
    private int _maxRunning;
    public int MaximumConcurrentBuilders => Volatile.Read(ref _maxRunning);

    public async Task<AgentRunResult> RunAsync(string directory, string prompt,
        string previousSession, CancellationToken cancellation, Action<string>? onSession = null,
        bool readOnly = false, Action<AgentTokenUsage>? onUsage = null)
    {
        if (readOnly)
        {
            onUsage?.Invoke(new AgentTokenUsage(10, 10));
            return new AgentRunResult { Success = true, UsageReported = true,
                InputTokens = 10, OutputTokens = 10, FinalText = "VERDICT: PASS\nReviewed" };
        }
        var running = Interlocked.Increment(ref _running);
        var previous = Volatile.Read(ref _maxRunning);
        while (running > previous &&
            Interlocked.CompareExchange(ref _maxRunning, running, previous) != previous)
            previous = Volatile.Read(ref _maxRunning);
        if (Interlocked.Increment(ref _started) == 2) _bothStarted.TrySetResult();
        try
        {
            await Task.WhenAny(_bothStarted.Task, Task.Delay(3000, cancellation));
            var name = prompt.Contains("Build A", StringComparison.Ordinal) ? "a.txt" : "b.txt";
            var src = Path.Combine(directory, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, name), "Generated by test");
            onUsage?.Invoke(new AgentTokenUsage(30, 20));
            return new AgentRunResult { Success = true, UsageReported = true,
                InputTokens = 30, OutputTokens = 20, FinalText = "Complete" };
        }
        finally { Interlocked.Decrement(ref _running); }
    }
}
