using Factory;
using Microsoft.Data.Sqlite;

internal static class RemoteProviderLeaseTests
{
    private static readonly string Base = new('a', 40);
    private static readonly string Head = new('b', 40);
    private static readonly string Tree = new('c', 40);

    public static async Task RunAsync()
    {
        const string worker = "lease-fixture";
        const string env = "FACTORY_REMOTE_EXECUTOR_TOKEN";
        var oldToken = Environment.GetEnvironmentVariable(env);
        var scratch = Directory.CreateTempSubdirectory("factory-remote-lease-");
        var token = new string('d', 64);
        Environment.SetEnvironmentVariable(env, token);
        try
        {
            var store = new StateStore(scratch.FullName);
            var project = new ProjectInfo { Name = "Lease fixture", Origin = "https://github.com/example/fixture",
                RequiredExecutionCapabilities = [RemoteProviderRouting.Capability] };
            var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 4000 };
            FactoryJob NewJob() => new() { ProjectId = project.Id, GoalId = goal.Id, Mode = JobExecutionModes.Remote,
                Title = "Change fixture", ExpectedPaths = ["fixture.txt"] };
            var job = NewJob();
            var settings = new FactorySettings { AllowPush = true, EnableRemoteProviderRouting = true,
                EnableRemoteProviderExecutor = true, RemoteProviderExecutorUrl = "http://127.0.0.1:9999",
                RemoteProviderTurnTokens = 256, GoalReservationTokens = 700, Runtime = "app-server",
                Providers = [new() { Model = "fixture-builder", TokenLimit = 1800 },
                    new() { Id = "review-fixture", Kind = "ollama", Model = "fixture:1", TokenLimit = 600 }] };
            settings.EnsureCredentials();
            store.Change(state =>
            {
                state.Projects.Add(project); state.Goals.Add(goal); state.Jobs.Add(job);
                state.Workers.Add(new WorkerInfo { Id = worker });
                state.WorkerProjectGrants.Add(new() { WorkerId = worker, ProjectId = project.Id });
                state.WorkerCapabilityProofs.AddRange(new[] { "native-agent", "git-cli", "runtime:app-server",
                    RemoteProviderRouting.Capability }.Select(capability => new WorkerCapabilityProof
                    { WorkerId = worker, Capability = capability, Evidence = "fixture", VerifiedAt = DateTimeOffset.UtcNow }));
                state.ProviderAssignments.Add(new() { ProjectId = project.Id, ProviderId = "review-fixture", Role = "review" });
            });
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(scratch.FullName, "factory.sqlite3"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "DROP TABLE remote_provider_turns; DROP TABLE remote_provider_leases; PRAGMA user_version=9;";
                command.ExecuteNonQuery();
            }
            store = new StateStore(scratch.FullName);
            Check(store.Job(job.Id).GoalId == goal.Id, "Schema 9 migration lost the existing job");
            Check(WorkerProtocol.Claim(store, settings, worker) is null, "Old protocol bypassed routing");
            settings.EnableRemoteProviderRouting = false;
            Check(WorkerProtocol.Claim(store, settings, worker, RemoteProviderRouting.ProtocolVersion) is null,
                "Disabled mixed routing accepted a claim");
            settings.EnableRemoteProviderRouting = true;
            var workerToken = WorkerAuthentication.CreateToken(settings.WorkerTokenKey, worker);
            foreach (var path in new[] { "/api/executor/turn", "/api/executor/turn/", "/API/Executor/Turn/",
                         "/api/executor/receipt", "/API/Executor/Receipt/" })
            {
                Check(WorkerAuthentication.Authorized("POST", path, "Bearer " + token, null, settings),
                    "Dedicated executor authentication failed for " + path);
                Check(!WorkerAuthentication.Authorized("POST", path, "Bearer " + settings.ApiToken, null, settings),
                    "Administrator token authorized the executor at " + path);
                Check(!WorkerAuthentication.Authorized("POST", path, "Bearer " + workerToken, worker, settings),
                    "Worker token authorized the executor at " + path);
                Check(!WorkerAuthentication.Authorized("GET", path, "Bearer " + token, null, settings),
                    "Unexpected executor request method was authorized");
            }
            Check(!WorkerAuthentication.Authorized("POST", "/api/executor/unknown/", "Bearer " + settings.ApiToken, null, settings),
                "An unknown executor endpoint inherited administrator authorization");
            Check(!WorkerAuthentication.Authorized("POST", "/api/worker/turn", "Bearer " + token, worker, settings),
                "Executor credential authorized a worker request");

            var claim = WorkerProtocol.Claim(store, settings, worker, RemoteProviderRouting.ProtocolVersion)
                ?? throw new Exception("Versioned worker could not claim the opted-in job");
            Check(claim.ProviderRoutes!.Single(r => r.Role == "review:code").ProviderId == "review-fixture",
                "Review route was not bound to the lease");
            var status = new WorkerStatus { WorkerId = worker, JobId = job.Id,
                ExecutionGeneration = claim.Job.ExecutionGeneration, LeaseToken = claim.Job.LeaseToken,
                ProviderLeaseId = claim.ProviderLeaseId, TokensIn = 4000, TokensOut = 4000, UsageComplete = true };
            Check(WorkerProtocol.Heartbeat(store, status, settings) && store.Job(job.Id).TokensIn == 0,
                "Worker heartbeat replaced trusted usage");

            RemoteProviderTurnRequest Request(string role, string commit) => new()
            {
                WorkerId = worker, JobId = claim.Job.Id, LeaseId = claim.ProviderLeaseId!.Value,
                TurnId = Guid.NewGuid(), Role = role, Commit = commit,
                ExecutionGeneration = claim.Job.ExecutionGeneration, LeaseToken = claim.Job.LeaseToken
            };
            RemoteExecutorGrant Begin(RemoteProviderTurnRequest request) =>
                store.Change(state => RemoteProviderRouting.Begin(state, settings, request).Grant);
            bool Record(RemoteExecutorGrant grant, RemoteExecutorReceipt receipt) =>
                store.Change(state => RemoteProviderRouting.Record(state, settings, grant, receipt));
            static RemoteExecutorReceipt Receipt(RemoteExecutorGrant grant, string? model = null) =>
                new(grant.TurnId, grant.LeaseId, grant.Route.ProviderId, grant.Route.Role, grant.Commit,
                    model ?? grant.Route.Model, 30, 10, true, true, "VERDICT: PASS", false, grant.Route.Role == "builder" ? Tree : "", []);

            var builderRequest = Request("builder", Base);
            var builder = Begin(builderRequest);
            Check(builder.VerifyRemoteBase && builder.Task.Contains(job.Title), "First builder was not bound to controller task");
            Reject(() => Begin(builderRequest), "duplicate turn dispatch");
            var receipt = Receipt(builder);
            Check(Record(builder, receipt) && !Record(builder, receipt), "Receipt was not fenced against replay");
            var code = Begin(Request("review:code", Head));
            var architecture = Begin(Request("review:architecture", Head));
            Reject(() => Begin(Request("review:security", Head)), "oversubscribed concurrent reviews");
            Check(code.ExpectedInputTree == Tree && code.BaseCommit == Base, "Review was not bound to builder tree and base");
            Check(Record(code, Receipt(code)) && Record(architecture, Receipt(architecture)), "Reviews did not settle");
            foreach (var role in new[] { "review:security", "review:ux" })
            {
                var grant = Begin(Request(role, Head));
                Check(Record(grant, Receipt(grant)), "Review did not settle");
            }
            Check(store.Read().ProviderUsages.Sum(u => u.InputTokens + u.OutputTokens) == 200 &&
                store.Job(job.Id).TokensIn == 150 && store.Job(job.Id).TokensOut == 50, "Ledgers diverged");
            status.Job = new FactoryJob { Id = job.Id, ProjectId = project.Id, Mode = JobExecutionModes.Remote,
                Attempt = claim.Job.Attempt, State = JobStates.PrReady, HeadSha = Head };
            var completedSnapshot = store.Read();
            Check(!RemoteProviderRouting.VerifyCompletion(completedSnapshot, completedSnapshot.RemoteProviderLeases.Single(),
                Head, Base), "Unverified GitHub tree was accepted");
            Check(await WorkerProtocol.CompleteAsync(store, status, new FixtureVerifier(), settings: settings) &&
                store.Job(job.Id).State == JobStates.PrReady, "Trusted mixed-provider completion was rejected");

            var nextJob = NewJob();
            store.Change(state => state.Jobs.Add(nextJob));
            claim = WorkerProtocol.Claim(store, settings, worker, RemoteProviderRouting.ProtocolVersion)!;
            var wrong = Begin(Request("builder", Base));
            Check(!Record(wrong, Receipt(wrong, "different-model")), "Substituted model was accepted");
            var recovered = new StateStore(scratch.FullName).Read();
            Check(recovered.Jobs.Single(j => j.Id == nextJob.Id).Stage == "reconcile" &&
                recovered.RemoteProviderTurns.Single(t => t.Id == wrong.TurnId).State == "held" &&
                recovered.BudgetReservations.Single(r => r.JobId == nextJob.Id).Consumed == 40,
                "Model mismatch did not fence the lease and retain observed usage");

            var expiringJob = NewJob();
            store.Change(state => state.Jobs.Add(expiringJob));
            claim = WorkerProtocol.Claim(store, settings, worker, RemoteProviderRouting.ProtocolVersion)!;
            var interrupted = Begin(Request("builder", Base));
            store.Change(state => state.Jobs.Single(j => j.Id == expiringJob.Id).LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1));
            Check(JobRecovery.QuarantineExpired(store, JobExecutionModes.Remote).Contains(expiringJob.Id) &&
                !Record(interrupted, Receipt(interrupted)), "Expired lease accepted late executor usage");
            var expired = store.Read();
            var heldTurn = expired.RemoteProviderTurns.Single(t => t.Id == interrupted.TurnId);
            Check(heldTurn.State == "held" && expired.ProviderBudgetReservations.Single(r => r.UsageId == heldTurn.UsageId).State == "held" &&
                expired.BudgetReservations.Single(r => r.JobId == expiringJob.Id).State == GoalBudgetLedger.Held,
                "Expiry released an uncertain liability");
            var restarted = new StateStore(scratch.FullName);
            var recovery = new RemoteReceiptRecoveryInput(true, "Verified durable executor receipt", false);
            RemoteProviderRouting.SettleRecoveredReceipt(restarted, expiringJob.Id, interrupted.TurnId, recovery, Receipt(interrupted));
            RemoteProviderRouting.SettleRecoveredReceipt(restarted, expiringJob.Id, interrupted.TurnId, recovery, Receipt(interrupted));
            var settled = restarted.Read();
            Check(settled.Jobs.Single(j => j.Id == expiringJob.Id) is { State: JobStates.Blocked, TokensIn: 30, TokensOut: 10 } &&
                settled.RemoteProviderTurns.Single(t => t.Id == interrupted.TurnId).State == "reconciled" &&
                settled.BudgetReservations.Single(r => r.JobId == expiringJob.Id) is { State: GoalBudgetLedger.Held, Consumed: 40 },
                "Receipt recovery reopened the lease or charged its usage twice");
            await VerifyExecutorAsync(store, settings, scratch.FullName, builder, code);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(scratch.FullName, "factory.sqlite3"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = """
                    ALTER TABLE remote_provider_turns DROP COLUMN grant_digest;
                    ALTER TABLE remote_provider_turns DROP COLUMN hold_reason;
                    PRAGMA user_version=10;
                    """;
                command.ExecuteNonQuery();
            }
            Check(new StateStore(scratch.FullName).Read().RemoteProviderTurns.Single(t => t.Id == interrupted.TurnId)
                is { State: "reconciled", GrantDigest: "", HoldReason: "" }, "Schema 10 migration lost an existing turn");
        }
        finally
        {
            Environment.SetEnvironmentVariable(env, oldToken);
            if (OperatingSystem.IsWindows())
                foreach (var file in scratch.EnumerateFiles("*", SearchOption.AllDirectories))
                    if (file.IsReadOnly) file.IsReadOnly = false;
            scratch.Delete(recursive: true);
        }
    }

    public static async Task VerifyMovingPullHeadAsync()
    {
        const string repository = "example/fixture";
        var project = new ProjectInfo
        {
            Origin = "https://github.com/" + repository, BaseBranch = "master",
            Checks = [new CheckCommand { Name = "build" }]
        };
        var job = new FactoryJob
        {
            ProjectId = project.Id, Mode = JobExecutionModes.Remote, Attempt = 1, State = JobStates.PrReady,
            HeadSha = Head, PrUrl = project.Origin + "/pull/1", ExpectedPaths = ["fixture.txt"]
        };
        job.Branch = $"factory/{job.Id:N}";
        var status = new WorkerStatus
        {
            Job = job, ProviderLeaseId = Guid.NewGuid(),
            Evidence = Json.Stringify(new
            {
                jobId = job.Id, job.Attempt, outcome = "ready", commit = Head,
                report = new
                {
                    passed = true, headSha = Head, findings = Array.Empty<string>(),
                    checks = new[] { new { name = "build", exitCode = 0 } },
                    reviews = new Dictionary<string, string>
                    {
                        ["code"] = "PASS\nfixture", ["architecture"] = "PASS\nfixture",
                        ["security"] = "PASS\nfixture", ["ux"] = "PASS\nfixture"
                    }
                }
            })
        };

        GitHubWorkerResultVerifier Verifier(bool forcePush, bool wrongCheckCommit = false, bool wrongBase = false,
            bool intermediateHead = false)
        {
            var currentHead = Head;
            return new GitHubWorkerResultVerifier((args, _, _) =>
            {
                string output;
                if (args is ["api", var commitPath] && commitPath == $"repos/{repository}/git/commits/{Head}")
                {
                    if (forcePush) currentHead = Base;
                    output = Json.Stringify(new { sha = Head, tree = new { sha = Tree } });
                }
                else if (args is ["api", var pullPath] && pullPath == $"repos/{repository}/pulls/1")
                {
                    var repo = new { full_name = repository };
                    output = Json.Stringify(new
                    {
                        state = "open", @base = new { repo, @ref = "master", sha = Base },
                        head = new { repo, @ref = job.Branch, sha = currentHead }
                    });
                }
                else if (args is ["api", var comparePath] && comparePath == $"repos/{repository}/compare/{Base}...{Head}?per_page=1")
                    output = Json.Stringify(new { base_commit = new { sha = wrongBase ? Tree : Base },
                        files = new[] { new { filename = "fixture.txt", status = "modified" } } });
                else if (args is ["api", "graphql", ..] && args.Contains("sha=" + Head))
                {
                    if (intermediateHead) currentHead = Base;
                    output = Json.Stringify(new { data = new { repository = new { @object = new {
                        oid = wrongCheckCommit ? Base : Head, statusCheckRollup = new { contexts = new {
                            pageInfo = new { hasNextPage = false },
                            // Checks at the intermediate head pass. The immutable original head is still pending.
                            nodes = new[] { new { name = "build", isRequired = true,
                                conclusion = intermediateHead ? null : "SUCCESS" } }
                        } }
                    } } } });
                }
                else
                    throw new Exception("Unexpected GitHub request: " + string.Join(" ", args));
                return Task.FromResult(new CommandResult(0, output, ""));
            });
        }

        var stable = await Verifier(forcePush: false).VerifyAsync(project, job, status, CancellationToken.None);
        Check(stable.TreeSha == Tree, "Stable PR did not return the verified tree");
        await RejectAsync(Verifier(forcePush: true), "head commit differs");
        await RejectAsync(Verifier(forcePush: false, wrongCheckCommit: true), "checks belong to a different commit");
        await RejectAsync(Verifier(forcePush: false, wrongBase: true), "different base commit");
        await RejectAsync(Verifier(forcePush: false, intermediateHead: true), "missing, pending or failing");

        async Task RejectAsync(GitHubWorkerResultVerifier verifier, string reason)
        {
            try { await verifier.VerifyAsync(project, job, status, CancellationToken.None); }
            catch (InvalidOperationException ex) when (ex.Message.Contains(reason, StringComparison.Ordinal)) { return; }
            throw new Exception("Expected GitHub verification to reject: " + reason);
        }
    }

    private static async Task VerifyExecutorAsync(StateStore store, FactorySettings settings, string scratch,
        RemoteExecutorGrant builder, RemoteExecutorGrant reviewer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        var repository = Directory.CreateDirectory(Path.Combine(scratch, "repository")).FullName;
        Task<string> Git(params string[] args) => RemoteProviderExecutor.GitAsync(repository, ct, args);
        async Task Commit(string message) => await Git("-c", "user.name=Factory Fixture", "-c",
            "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", message);
        async Task<byte[]> Bundle()
        {
            var path = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".bundle");
            await Git("bundle", "create", path, "HEAD");
            return await File.ReadAllBytesAsync(path, ct);
        }
        await Git("init", "-b", "master");
        await File.WriteAllTextAsync(Path.Combine(repository, "fixture.txt"), "original\n", ct);
        await Git("add", "fixture.txt");
        await Commit("Base fixture");
        var baseCommit = await Git("rev-parse", "HEAD");
        var builderGrant = builder with
        {
            TurnId = Guid.NewGuid(), Commit = baseCommit, BaseCommit = baseCommit, VerifyRemoteBase = false,
            ExpectedInputTree = await Git("write-tree"), Bundle = await Bundle()
        };
        var built = await RemoteProviderExecutor.RunAsync(store, settings, builderGrant, ct,
            new FixtureAgent(builder.Route.Model, expectReadOnly: false));
        Check(built.Success && built.Patch.Length != 0 && built.ObservedModel == builder.Route.Model,
            "Executor did not produce a model-bound patch");
        var restarted = new StateStore(store.Home);
        var saved = await RemoteProviderExecutor.ReadReceiptAsync(restarted,
            new(builderGrant.TurnId, RemoteProviderExecutor.Digest(builderGrant)), ct);
        Check(saved is { FinalUsage: true } && saved.OutputTree == built.OutputTree && saved.Patch.SequenceEqual(built.Patch),
            "Executor restart lost the durable receipt");
        Check(await RemoteProviderExecutor.ReadReceiptAsync(restarted,
            new(builderGrant.TurnId, RemoteProviderExecutor.Digest(builderGrant with { Budget = builderGrant.Budget + 1 })), ct) is null,
            "A conflicting grant retrieved another turn's receipt");
        var patch = Path.Combine(scratch, "executor.patch");
        await File.WriteAllBytesAsync(patch, built.Patch, ct);
        await Git("apply", "--index", "--binary", patch);
        Check(await Git("write-tree") == built.OutputTree, "Executor patch did not reproduce the output tree");
        await Commit("Executor change");
        var reviewGrant = reviewer with
        {
            TurnId = Guid.NewGuid(), Commit = await Git("rev-parse", "HEAD"), BaseCommit = baseCommit,
            ExpectedInputTree = built.OutputTree, Bundle = await Bundle()
        };
        try
        {
            RemoteGitBundle.Validate(reviewGrant.Bundle, ct, maxExpandedBytes: 64);
            throw new Exception("Executor accepted a bundle over the expanded object limit");
        }
        catch (InvalidDataException) { }
        var foreign = await Git("-c", "user.name=Factory Fixture", "-c", "user.email=fixture@example.invalid",
            "commit-tree", built.OutputTree, "-m", "Foreign history with the same tree");
        await Git("checkout", "--detach", foreign);
        var foreignGrant = reviewGrant with { TurnId = Guid.NewGuid(), Commit = foreign, Bundle = await Bundle() };
        await Git("checkout", "--detach", reviewGrant.Commit);
        try
        {
            await RemoteProviderExecutor.RunAsync(store, settings, foreignGrant, ct,
                new FixtureAgent(reviewer.Route.Model, expectReadOnly: true));
            throw new Exception("Executor accepted foreign history with the same tree");
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Executor Git command", StringComparison.Ordinal)) { }
        var reviewed = await RemoteProviderExecutor.RunAsync(store, settings, reviewGrant, ct,
            new FixtureAgent(reviewer.Route.Model, expectReadOnly: true));
        Check(reviewed.Success && reviewed.FinalText == "VERDICT: PASS" && reviewed.Patch.Length == 0,
            "Executor did not bind the review to the committed patch");
        try
        {
            await RemoteProviderExecutor.RunAsync(store, settings, reviewGrant, ct,
                new FixtureAgent(reviewer.Route.Model, expectReadOnly: true));
            throw new Exception("Executor dispatched the same grant twice");
        }
        catch (IOException) { }

        await File.WriteAllBytesAsync(Path.Combine(repository, "compressible.bin"), new byte[65536], ct);
        await Git("add", "compressible.bin");
        await Commit("Highly compressible object");
        var compressed = await Bundle();
        Check(compressed.Length < 16 * 1024, "Fixture bundle was larger than the expanded test limit");
        try
        {
            RemoteGitBundle.Validate(compressed, ct, maxExpandedBytes: 16 * 1024);
            throw new Exception("Executor accepted an oversized expanded object from a small bundle");
        }
        catch (InvalidDataException) { }
    }

    private sealed class FixtureAgent(string model, bool expectReadOnly) : IAgentRuntime
    {
        public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
            CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
            Action<AgentTokenUsage>? onUsage = null)
        {
            Check(readOnly == expectReadOnly, "Executor did not enforce the role");
            if (readOnly) Check(prompt.Contains("+changed by executor"), "Review prompt omitted the independently read diff");
            else await File.WriteAllTextAsync(Path.Combine(directory, "fixture.txt"), "changed by executor\n", cancellation);
            return new AgentRunResult { ObservedModel = model, InputTokens = 30, OutputTokens = 10,
                UsageReported = true, Success = true, FinalText = "VERDICT: PASS" };
        }
    }

    private sealed class FixtureVerifier : IWorkerResultVerifier
    {
        public Task<VerifiedWorkerResult> VerifyAsync(ProjectInfo project, FactoryJob job, WorkerStatus status,
            CancellationToken ct) => Task.FromResult(new VerifiedWorkerResult("{}", Tree));
    }

    private static void Reject(Action action, string scenario)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Unexpectedly accepted " + scenario);
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }
}
