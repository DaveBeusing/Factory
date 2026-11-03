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
        Assert(Count("PRAGMA user_version;") == 4, "Unexpected relational schema version");
        Assert(Count("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='factory_state';") == 0);
        foreach (var table in new[] { "factory_metadata", "projects", "goals", "jobs", "events",
            "workers", "releases", "knowledge_items", "graph_nodes", "evaluations",
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
        var runtime = new BudgetedAgentRuntime(scripted, store, job.Id);
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
        Assert(Convert.ToInt64(query.ExecuteScalar()) == 3);
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

var passed = results.Count(x => x.Passed);
Console.WriteLine($"{passed}/{results.Count} passed");
return passed == results.Count ? 0 : 1;

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
