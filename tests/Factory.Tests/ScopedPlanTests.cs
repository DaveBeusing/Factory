using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Factory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

internal static class ScopedPlanTests
{
    internal static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var root = Path.Combine(Path.GetTempPath(), "factory-scoped-plans-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = Path.Combine(root, "repo");
            Directory.CreateDirectory(repository);
            Git("init", "-b", "master");
            Commit("Initial master");
            var home = Path.Combine(root, "state");
            var store = new StateStore(home);
            var first = new ProjectInfo { Name = "First", Path = repository };
            var second = new ProjectInfo { Name = "Second" };
            var goal = new FactoryGoal { ProjectId = first.Id, Objective = "Draft planning fixture" };
            var foreign = new FactoryGoal { ProjectId = second.Id, Objective = "Foreign draft" };
            store.Change(state =>
            {
                state.Projects.AddRange([first, second]);
                state.Goals.AddRange([goal, foreign]);
            });
            var alice = store.CreateViewer("Alice");
            var bob = store.CreateViewer("Bob");
            store.SetViewerGrant(alice.SubjectId, first.Id, grant: true, planner: true);
            store.SetViewerGrant(bob.SubjectId, first.Id, grant: true);
            store.SetViewerGrant(bob.SubjectId, second.Id, grant: true, planner: true);
            var calls = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var enteredRegrant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRegrant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            await using var provider = builder.Build();
            provider.Urls.Add("http://127.0.0.1:0");
            provider.MapPost("/api/generate", async (HttpRequest request) =>
            {
                using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: timeout.Token);
                Require(body.RootElement.GetProperty("model").GetString() == "fixture-model", "Wrong planner model");
                Require(!body.RootElement.GetProperty("prompt").GetString()!.Contains(foreign.Objective,
                    StringComparison.Ordinal), "Foreign objective reached the provider");
                var call = Interlocked.Increment(ref calls);
                if (call == 2)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(timeout.Token);
                }
                if (call == 3)
                {
                    enteredRegrant.TrySetResult();
                    await releaseRegrant.Task.WaitAsync(timeout.Token);
                }
                return new
                {
                    model = "fixture-model", done = true,
                    response = """{"packages":[{"title":"Provider package","description":"Fixture","files":["README.md"]}]}""",
                    prompt_eval_count = 30, eval_count = 10
                };
            });
            await provider.StartAsync(timeout.Token);
            var profile = new ProviderProfile
            {
                Id = "fixture-planner", Kind = "ollama", Model = "fixture-model", Endpoint = provider.Urls.Single()
            };
            var settings = new FactorySettings { Providers = [profile], GoalReservationTokens = 512 };
            settings.EnsureCredentials();
            File.WriteAllText(Path.Combine(home, "settings.json"), Json.Stringify(settings));
            store.Change(state => state.ProviderAssignments.Add(new()
            {
                ProjectId = first.Id, Role = "planner", ProviderId = profile.Id
            }));
            using var dashboard = await RemoteRestoreTests.DashboardProcess.StartAsync(
                home, "unused-executor-test-token", "http://127.0.0.1:0", timeout.Token);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(dashboard.Address)
            };
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(home, "factory.sqlite3"), Pooling = false
            }.ToString());
            db.Open();

            var project = $"/api/viewer/projects/{first.Id}";
            var path = project + $"/plans/{goal.Id}";
            var foreignPath = project + $"/plans/{foreign.Id}";
            var otherPath = $"/api/viewer/projects/{second.Id}/plans/{foreign.Id}";
            var view = await Request(alice.Token, HttpMethod.Get, path, HttpStatusCode.OK);
            using var payload = JsonDocument.Parse(view);
            var goalRevision = payload.RootElement.GetProperty("goalRevision").GetString()!;
            var draft = new FactoryPlan
            {
                GoalRevision = goalRevision,
                Packages = [new PlannedPackage { Title = "First package", Description = "Review the fixture scope", Files = ["README.md"] }]
            };
            await Request(bob.Token, HttpMethod.Put, path, HttpStatusCode.Forbidden, draft);
            await Request(alice.Token, HttpMethod.Get, otherPath, HttpStatusCode.NotFound);
            await Request(alice.Token, HttpMethod.Put, foreignPath, HttpStatusCode.NotFound, draft);
            await Request(alice.Token, HttpMethod.Post, foreignPath + "/adopt", HttpStatusCode.NotFound, new PlanAdoptInput());
            draft.GoalId = foreign.Id;
            await Request(alice.Token, HttpMethod.Put, path, HttpStatusCode.NotFound, draft);
            draft.GoalId = goal.Id;
            draft.ProjectId = second.Id;
            await Request(alice.Token, HttpMethod.Put, path, HttpStatusCode.NotFound, draft);
            draft.ProjectId = first.Id;
            Require(AuditCount() == 0, "Denied requests wrote planning audit entries");

            RejectAudit();
            await Request(alice.Token, HttpMethod.Put, path, HttpStatusCode.InternalServerError, draft);
            Require(MissionControlManager.ReadPlan(store, goal.Id) is null && AuditCount() == 0, "Plan save escaped audit rollback");
            Execute("DROP TRIGGER reject_plan_audit;");

            var preflight = WorkPackagePreflight.Inspect(store.Read(), first, goal);
            var legacy = new FactoryPlan
            {
                GoalId = goal.Id, ProjectId = first.Id, GoalRevision = goalRevision, Revision = Guid.NewGuid(),
                BaseCommit = preflight.BaseCommit, Preflight = preflight,
                Packages = [new PlannedPackage { Title = "Reviewed legacy package", Description = "Legacy work", Files = ["README.md"] }]
            };
            var legacyPath = Planning.PathFor(store, goal.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            File.WriteAllText(legacyPath, Json.Stringify(legacy));
            var legacyView = JsonSerializer.Deserialize<ScopedPlanView>(
                await Request(alice.Token, HttpMethod.Get, path, HttpStatusCode.OK), Json.Options)!;
            Require(legacyView.IsLegacy, "Legacy plan was not identified as requiring a new revision");
            var reviewedLegacy = legacyView.Plan!;
            Require(reviewedLegacy.Packages.Single().Title == "Reviewed legacy package", "Legacy plan was not readable");
            legacy.Packages[0].Title = "Changed after review";
            File.WriteAllText(legacyPath, Json.Stringify(legacy));
            await Adopt(reviewedLegacy.Revision, HttpStatusCode.Conflict);
            try
            {
                Planning.Adopt(store, goal.Id, "local", reviewedExistingWork: true, planRevision: reviewedLegacy.Revision!.Value);
                throw new Exception("Administrator adopted a mutable legacy file");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Save the legacy plan", StringComparison.Ordinal)) { }
            Require(store.Read().Jobs.Count == 0 && AuditCount() == 0, "Rejected legacy adoption wrote state");

            draft.Revision = reviewedLegacy.Revision;
            var firstPlan = await Save();
            Require(firstPlan.Revision != reviewedLegacy.Revision, "A saved legacy plan needs a new immutable revision");
            await Adopt(reviewedLegacy.Revision, HttpStatusCode.Conflict);
            Require(MissionControlManager.ReadPlan(store, goal.Id)?.Revision == firstPlan.Revision, "Administrator cannot read scoped revision");
            draft.Revision = firstPlan.Revision;
            draft.Packages[0].Title = "Revised package";
            var secondPlan = await Save();
            await Request(alice.Token, HttpMethod.Put, path, HttpStatusCode.Conflict, draft);
            await Adopt(firstPlan.Revision, HttpStatusCode.Conflict);
            await Adopt(secondPlan.Revision, HttpStatusCode.BadRequest, reviewed: false);

            store.Change(state => state.Goals.Single(item => item.Id == goal.Id).AcceptanceCriteria.Add("New criterion"));
            await Adopt(secondPlan.Revision, HttpStatusCode.Conflict);
            draft.Revision = secondPlan.Revision;
            await Request(alice.Token, HttpMethod.Put, path, HttpStatusCode.Conflict, draft);
            draft.GoalRevision = Planning.GoalRevision(store.Read().Goals.Single(item => item.Id == goal.Id));
            var thirdPlan = await Save();
            Commit("Advance master");
            await Adopt(thirdPlan.Revision, HttpStatusCode.Conflict);
            draft.Revision = thirdPlan.Revision;
            var current = await Save();
            Require(AuditCount() == 4 && store.Read().Jobs.Count == 0, "Rejected adoption wrote state");

            RejectAudit();
            await Adopt(current.Revision, HttpStatusCode.InternalServerError);
            var rolledBack = store.Read();
            Require(rolledBack.Jobs.Count == 0 && rolledBack.Goals.Single(item => item.Id == goal.Id).State == "draft",
                "Adoption escaped audit rollback");
            Execute("DROP TRIGGER reject_plan_audit;");
            await Adopt(current.Revision, HttpStatusCode.OK);
            await Adopt(current.Revision, HttpStatusCode.Conflict);
            var final = store.Read();
            Require(final.Jobs.Single().ProjectId == first.Id && final.Jobs.Single().GoalId == goal.Id &&
                final.Jobs.Single().Title == "Revised package" &&
                final.Goals.Single(item => item.Id == goal.Id).State == "active" && AuditCount() == 5,
                "Reviewed adoption did not commit expected state and audit");
            using var audit = db.CreateCommand();
            audit.CommandText = """
                SELECT subject_id, project_id, target_id, plan_revision, reviewed_existing_work
                FROM project_planning_audit WHERE operation='plan_adopted';
                """;
            using var row = audit.ExecuteReader();
            Require(row.Read() && Guid.Parse(row.GetString(0)) == alice.SubjectId &&
                Guid.Parse(row.GetString(1)) == first.Id && Guid.Parse(row.GetString(2)) == goal.Id &&
                Guid.Parse(row.GetString(3)) == current.Revision && row.GetBoolean(4) && !row.Read(),
                "Adoption audit did not bind the server-resolved actor and reviewed revision");
            row.Close();

            var fundedGoal = new FactoryGoal { ProjectId = first.Id, Objective = "Provider draft fixture", TokenBudget = 1 };
            store.Change(state => state.Goals.Add(fundedGoal));
            var generate = project + $"/plans/{fundedGoal.Id}/generate";
            var generation = new ScopedPlanGenerationInput { GoalRevision = Planning.GoalRevision(fundedGoal) };
            await Request(bob.Token, HttpMethod.Post, generate, HttpStatusCode.Forbidden, generation);
            await Request(alice.Token, HttpMethod.Post, foreignPath + "/generate", HttpStatusCode.NotFound, generation);
            await Request(alice.Token, HttpMethod.Post, otherPath + "/generate", HttpStatusCode.NotFound, generation);
            await Request(alice.Token, HttpMethod.Post, generate, HttpStatusCode.Conflict, generation);
            Require(calls == 0 && store.Read().ProviderUsages.Count == 0 && AuditCount() == 5,
                "Denied generation called the provider, charged usage or wrote audit");
            store.Change(state => state.Goals.Single(item => item.Id == fundedGoal.Id).TokenBudget = 4096);
            RejectAudit();
            await Request(alice.Token, HttpMethod.Post, generate, HttpStatusCode.InternalServerError, generation);
            Require(calls == 0 && store.Read().ProviderUsages.Count == 0, "Audit failure left a reservation or invoked the provider");
            Execute("DROP TRIGGER reject_plan_audit;");
            var generated = JsonSerializer.Deserialize<FactoryPlan>(
                await Request(alice.Token, HttpMethod.Post, generate, HttpStatusCode.OK, generation), Json.Options)!;
            Require(generated.Packages.Single().Title == "Provider package" && generated.ProjectId == first.Id &&
                generated.GoalId == fundedGoal.Id && AuditCount() == 7, "Generation did not commit the scoped plan and audit");
            generation.Revision = generated.Revision;
            var running = Request(alice.Token, HttpMethod.Post, generate, HttpStatusCode.NotFound, generation);
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                store.SetViewerGrant(alice.SubjectId, first.Id, grant: false, planner: true);
            }
            finally { release.TrySetResult(); }
            await running;
            await Request(alice.Token, HttpMethod.Post, generate, HttpStatusCode.NotFound, generation);
            var afterRevocation = store.Read();
            Require(calls == 2 && afterRevocation.ProviderUsages.Count == 2 &&
                afterRevocation.ProviderUsages.All(usage => usage.ProjectId == first.Id && usage.GoalId == fundedGoal.Id &&
                    usage.ProviderId == profile.Id && usage.Model == "fixture-model" && usage.Role == "planner" &&
                    usage.InputTokens == 30 && usage.OutputTokens == 10 && usage.FinalUsageReported) &&
                afterRevocation.ProviderBudgetReservations.Count == 2 &&
                afterRevocation.ProviderBudgetReservations.All(reservation => reservation.State == "released") &&
                GoalBudgetLedger.Available(afterRevocation, fundedGoal.Id) == 4016,
                "Confirmed provider and goal usage must settle even after revocation");
            Require(MissionControlManager.ReadPlan(store, fundedGoal.Id)?.Revision == generated.Revision && AuditCount() == 8,
                "Revoked output escaped the publication fence");

            store.SetViewerGrant(alice.SubjectId, first.Id, grant: true, planner: true);
            var regranted = Request(alice.Token, HttpMethod.Post, generate, HttpStatusCode.Conflict, generation);
            try
            {
                await enteredRegrant.Task.WaitAsync(timeout.Token);
                store.SetViewerGrant(alice.SubjectId, first.Id, grant: false, planner: true);
                store.SetViewerGrant(alice.SubjectId, first.Id, grant: true, planner: true);
            }
            finally { releaseRegrant.TrySetResult(); }
            await regranted;
            var afterRegrant = store.Read();
            Require(MissionControlManager.ReadPlan(store, fundedGoal.Id)?.Revision == generated.Revision && AuditCount() == 9 &&
                afterRegrant.ProviderUsages.Count == 3 && afterRegrant.ProviderUsages.All(usage => usage.FinalUsageReported) &&
                afterRegrant.ProviderBudgetReservations.All(reservation => reservation.State == "released") &&
                GoalBudgetLedger.Available(afterRegrant, fundedGoal.Id) == 3976,
                "Regrant must fence the old output and preserve its confirmed usage");
            var fresh = JsonSerializer.Deserialize<FactoryPlan>(
                await Request(alice.Token, HttpMethod.Post, generate, HttpStatusCode.OK, generation), Json.Options)!;
            Require(calls == 4 && fresh.Revision != generated.Revision && AuditCount() == 11,
                "The new grant must authorize a fresh request");
            using var reservationAudit = db.CreateCommand();
            reservationAudit.CommandText = """
                SELECT COUNT(*) FROM project_planning_audit a JOIN provider_usages u ON u.id=a.target_id
                WHERE a.operation='plan_generation_reserved' AND a.subject_id=$actor AND a.project_id=u.project_id;
                """;
            reservationAudit.Parameters.AddWithValue("$actor", alice.SubjectId.ToString("D"));
            Require((long)reservationAudit.ExecuteScalar()! == 4, "Reservation audit is not bound to its actor and usage");

            using (var lifecycle = db.CreateCommand())
            {
                lifecycle.CommandText = """
                    SELECT COUNT(*) FROM lifecycle_audit
                    WHERE project_id=$project AND entity_id=$goal AND actor=$actor
                      AND ((entity_kind='plan' AND resulting_state='adopted')
                        OR (entity_kind='approval' AND resulting_state='approved'));
                    """;
                lifecycle.Parameters.AddWithValue("$project", first.Id.ToString());
                lifecycle.Parameters.AddWithValue("$goal", goal.Id.ToString());
                lifecycle.Parameters.AddWithValue("$actor", alice.SubjectId.ToString());
                Require((long)lifecycle.ExecuteScalar()! == 2, "The reviewed plan and approval lack correlated audit records");
            }

            store.SetOperatorGrant(bob.SubjectId, first.Id, grant: true);
            var local = new FactoryJob { ProjectId = first.Id, Title = "Running local fixture", Priority = 100, ExpectedPaths = ["control.txt"] };
            var foreignJob = new FactoryJob { ProjectId = second.Id };
            store.Change(state =>
            {
                state.Projects.Single(item => item.Id == first.Id).Checks.Add(new()
                    { Name = "head", Executable = "git", Arguments = ["rev-parse", "HEAD"] });
                state.Jobs.AddRange([local, foreignJob]);
            });
            var runtime = new PausedControlRuntime();
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var engine = new FactoryEngine(store, new FactorySettings { LeaseSeconds = 15 }, agent: runtime);
            var executionTask = engine.RunOneAsync(local.Id, execution.Token);
            var jobUrl = project + $"/jobs/{local.Id}";
            try
            {
                var enteredExecution = await Task.WhenAny(runtime.Entered.Task, executionTask).WaitAsync(timeout.Token);
                if (enteredExecution == executionTask) await executionTask;
                Require(runtime.Entered.Task.IsCompletedSuccessfully, "Local runner did not reach the provider");
                await Request(alice.Token, HttpMethod.Post, jobUrl + "/action", HttpStatusCode.Forbidden, new { action = "stop" });
                await Request(bob.Token, HttpMethod.Post, project + $"/jobs/{foreignJob.Id}/action",
                    HttpStatusCode.NotFound, new { action = "stop" });
                RejectOperationAudit();
                await Request(bob.Token, HttpMethod.Post, jobUrl + "/action", HttpStatusCode.InternalServerError,
                    new { action = "stop" });
                Require(store.Job(local.Id).RequestedState == "", "Rejected control left an interrupt request");
                Execute("DROP TRIGGER reject_operation_audit;");
                var observed = store.Job(local.Id);
                var staleRevision = JobLifecycle.ControlRevision(local.Id, observed.ExecutionGeneration,
                    observed.State, observed.RequestedState, observed.UpdatedAt.AddTicks(-1));
                await Request(bob.Token, HttpMethod.Post, jobUrl + "/action", HttpStatusCode.Conflict,
                    new { action = "cancel", controlRevision = staleRevision });
                Require(store.Job(local.Id).RequestedState == "", "Stale cancellation targeted the current lease");
                var accepted = JsonSerializer.Deserialize<ProjectJobControlResult>(
                    await Request(bob.Token, HttpMethod.Post, jobUrl + "/action", HttpStatusCode.Accepted,
                        new { action = "cancel", reason = "Cancel fixture", controlRevision = observed.ControlRevision }), Json.Options)!;
                Require(accepted.RequestedState == JobStates.Cancelled, "Active cancellation did not expose its requested outcome");
                await executionTask.WaitAsync(timeout.Token);
            }
            finally
            {
                execution.Cancel();
                await executionTask;
            }
            var stopped = store.Job(local.Id);
            Require(stopped.State == JobStates.Cancelled && stopped.LeaseOwner == "" && stopped.RequestedState == "",
                "The runner did not consume the durable cancellation request");
            await Request(bob.Token, HttpMethod.Post, jobUrl + "/action", HttpStatusCode.OK, new { action = "cancel", controlRevision = stopped.ControlRevision });
            await Request(bob.Token, HttpMethod.Post, jobUrl + "/action", HttpStatusCode.Conflict, new { action = "resume" });
            using (var lifecycle = db.CreateCommand())
            {
                lifecycle.CommandText = """
                    SELECT COUNT(*) FROM lifecycle_audit WHERE entity_id=$job
                      AND (reason_code='CancelRequested' OR reason_code='ExecutionCancelled');
                    """;
                lifecycle.Parameters.AddWithValue("$job", local.Id.ToString());
                Require((long)lifecycle.ExecuteScalar()! == 2, "Cancellation replay duplicated or lost a decision");
            }

            var recovery = new FactoryJob { ProjectId = first.Id, ExpectedPaths = ["control.txt"] };
            store.Change(state =>
            {
                state.Jobs.Add(recovery);
                JobRecovery.Assign(recovery, "fixture-recovery", 120, local: true);
                recovery.Worktree = repository;
                JobRecovery.Checkpoint(recovery, "agent", GitOperations.Head(repository));
                recovery.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
            });
            Require(JobRecovery.QuarantineExpired(store).Contains(recovery.Id), "Recovery fixture was not quarantined");
            var operationsJson = await Request(bob.Token, HttpMethod.Get, project + "/jobs/operations", HttpStatusCode.OK);
            var operationsView = JsonSerializer.Deserialize<List<ProjectJobOperation>>(operationsJson, Json.Options)!;
            var recoveryRevision = operationsView.Single(job => job.Id == recovery.Id).RecoveryRevision;
            Require(recoveryRevision is not null, "Missing recovery checkpoint revision");
            var review = new { reviewed = true, reason = "Verified runner and clean worktree", recoveryRevision };
            var recoveryUrl = project + $"/jobs/{recovery.Id}/reconcile";
            await Request(bob.Token, HttpMethod.Post, project + $"/jobs/{foreignJob.Id}/reconcile",
                HttpStatusCode.NotFound, review);
            await Request(bob.Token, HttpMethod.Post, recoveryUrl, HttpStatusCode.BadRequest, new { reviewed = false });
            await Request(bob.Token, HttpMethod.Post, recoveryUrl, HttpStatusCode.BadRequest, new { review.reviewed, review.reason });
            await Request(bob.Token, HttpMethod.Post, recoveryUrl, HttpStatusCode.Conflict, review);
            // The live fixture PID now represents a reused PID, not the old runner.
            store.UpdateJob(recovery.Id, job => job.WorkerProcessStartedAt = job.WorkerProcessStartedAt!.Value.AddDays(-1));
            var dirty = Path.Combine(repository, "dirty.txt");
            File.WriteAllText(dirty, "untracked");
            await Request(bob.Token, HttpMethod.Post, recoveryUrl, HttpStatusCode.Conflict, review);
            File.Delete(dirty);
            store.UpdateJob(recovery.Id, job => job.ExecutionGeneration++);
            await Request(bob.Token, HttpMethod.Post, recoveryUrl, HttpStatusCode.Conflict, review);
            Require(store.Job(recovery.Id).State == JobStates.Blocked, "A newer execution accepted an older checkpoint review");
            operationsJson = await Request(bob.Token, HttpMethod.Get, project + "/jobs/operations", HttpStatusCode.OK);
            operationsView = JsonSerializer.Deserialize<List<ProjectJobOperation>>(operationsJson, Json.Options)!;
            review = review with { recoveryRevision = operationsView.Single(job => job.Id == recovery.Id).RecoveryRevision };
            RejectOperationAudit();
            await Request(bob.Token, HttpMethod.Post, recoveryUrl, HttpStatusCode.InternalServerError, review);
            Require(store.Job(recovery.Id).State == JobStates.Blocked, "Recovery escaped audit rollback");
            Execute("DROP TRIGGER reject_operation_audit;");
            await Request(bob.Token, HttpMethod.Post, recoveryUrl, HttpStatusCode.OK, review);
            Require(store.Job(recovery.Id).Stage == "reconciled" && store.Job(recovery.Id).State == JobStates.Queued &&
                store.Job(foreignJob.Id).State == JobStates.Queued, "Scoped recovery changed an unexpected job");
            store.SetOperatorGrant(bob.SubjectId, first.Id, grant: false);
            await Request(bob.Token, HttpMethod.Post, recoveryUrl, HttpStatusCode.Forbidden, review);
            using var operations = db.CreateCommand();
            operations.CommandText = """
                SELECT job_id,operation,reviewed,reason FROM project_operation_audit
                WHERE subject_id=$actor AND project_id=$project ORDER BY id;
                """;
            operations.Parameters.AddWithValue("$actor", bob.SubjectId.ToString("D"));
            operations.Parameters.AddWithValue("$project", first.Id.ToString("D"));
            using var op = operations.ExecuteReader();
            Require(op.Read() && Guid.Parse(op.GetString(0)) == local.Id && op.GetString(1) == "cancel_requested" &&
                !op.GetBoolean(2) && op.GetString(3) == "Cancel fixture" &&
                op.Read() && Guid.Parse(op.GetString(0)) == recovery.Id && op.GetString(1) == "reconcile" &&
                op.GetBoolean(2) && op.GetString(3) == review.reason && !op.Read(),
                "Active control and recovery audit did not bind the actor, project and evidence");

            async Task<FactoryPlan> Save() => JsonSerializer.Deserialize<FactoryPlan>(
                await Request(alice.Token, HttpMethod.Put, path, HttpStatusCode.OK, draft), Json.Options)!;
            Task<string> Adopt(Guid? revision, HttpStatusCode status, bool reviewed = true) =>
                Request(alice.Token, HttpMethod.Post, path + "/adopt", status,
                    new PlanAdoptInput { PlanRevision = revision!.Value, ReviewedExistingWork = reviewed });

            async Task<string> Request(string token, HttpMethod method, string url, HttpStatusCode status, object? body = null)
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body is not null && url.EndsWith("/action", StringComparison.Ordinal) &&
                    System.Text.RegularExpressions.Regex.Match(url, @"/jobs/([0-9a-f-]+)/action$") is { Success: true } match &&
                    Guid.TryParse(match.Groups[1].Value, out var targetId))
                {
                    var fields = System.Text.Json.JsonSerializer.SerializeToNode(body)!.AsObject();
                    if (!fields.ContainsKey("controlRevision"))
                        fields["controlRevision"] = store.Job(targetId).ControlRevision;
                    body = fields;
                }
                if (body is not null) request.Content = JsonContent.Create(body);
                using var response = await client.SendAsync(request, timeout.Token);
                var content = await response.Content.ReadAsStringAsync(timeout.Token);
                Require(response.StatusCode == status, $"{method} {url}: expected {status}, got {response.StatusCode}: {content}");
                return content;
            }

            void RejectAudit() => Execute("""
                CREATE TRIGGER reject_plan_audit BEFORE INSERT ON project_planning_audit
                BEGIN SELECT RAISE(ABORT, 'Fixture audit failure'); END;
                """);
            void RejectOperationAudit() => Execute("""
                CREATE TRIGGER reject_operation_audit BEFORE INSERT ON project_operation_audit
                BEGIN SELECT RAISE(ABORT, 'Fixture operation audit failure'); END;
                """);
            void Execute(string sql)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            long AuditCount()
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM project_planning_audit;";
                return (long)command.ExecuteScalar()!;
            }
            void Commit(string message) => Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
                "commit", "--allow-empty", "-m", message);
            void Git(params string[] args)
            {
                var result = Commands.Run("git", args, repository, 20);
                Require(result.Ok, "Git fixture failed: " + result.Error);
            }
        }
        finally
        {
            var objects = Path.Combine(root, "repo", ".git", "objects");
            if (OperatingSystem.IsWindows() && Directory.Exists(objects))
            {
                foreach (var file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PausedControlRuntime : IAgentRuntime
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
            CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
            Action<AgentTokenUsage>? onUsage = null)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException("The fixture provider must be interrupted");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
