using Factory;

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
        new PlannedPackage { Title="A", Description="a", DependsOn=[1] },
        new PlannedPackage { Title="B", Description="b", DependsOn=[0] }
    } };
    try { Planning.Validate(plan); throw new Exception("Cycle not rejected"); }
    catch (ArgumentException) { }
});
Test("Unauthenticated worker blocked", () => Assert(!WorkerProtocol.Authorized("Bearer invalid", "secret")));
Test("Authorized worker accepted", () => Assert(WorkerProtocol.Authorized("Bearer abc", "abc")));
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
        engine.VerifyIntegration(job.Id);
        Assert(store.Job(job.Id).Integrated, "Verified merge not recorded");
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
            Title = "Refine module", Description = "Refactor carefully" }, new FactorySettings());
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
                DependsOn = [other.Id] }, new FactorySettings());
            throw new Exception("Cross-project dependency accepted");
        }
        catch (ArgumentException) { }
    }
    finally { try { Directory.Delete(home, true); } catch { } }
});

var passed = results.Count(x => x.Passed);
Console.WriteLine($"{passed}/{results.Count} passed");
return passed == results.Count ? 0 : 1;

internal sealed class FakeAgent : IAgentRuntime
{
    public Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false)
    {
        onSession?.Invoke("fake-session");
        if (readOnly) return Task.FromResult(new AgentRunResult { Success = true, FinalText = "VERDICT: PASS\nNo blocking issues", SessionId = "fake-session" });
        var src = Path.Combine(directory, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "generated.txt"), "Tested content");
        return Task.FromResult(new AgentRunResult { Success = true, FinalText = "Implementation complete", SessionId = "fake-session" });
    }
}
