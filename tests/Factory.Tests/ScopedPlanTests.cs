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
                if (Interlocked.Increment(ref calls) == 2)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(timeout.Token);
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
            var reviewedLegacy = JsonSerializer.Deserialize<ScopedPlanView>(
                await Request(alice.Token, HttpMethod.Get, path, HttpStatusCode.OK), Json.Options)!.Plan!;
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
            using var reservationAudit = db.CreateCommand();
            reservationAudit.CommandText = """
                SELECT COUNT(*) FROM project_planning_audit a JOIN provider_usages u ON u.id=a.target_id
                WHERE a.operation='plan_generation_reserved' AND a.subject_id=$actor AND a.project_id=u.project_id;
                """;
            reservationAudit.Parameters.AddWithValue("$actor", alice.SubjectId.ToString("D"));
            Require((long)reservationAudit.ExecuteScalar()! == 2, "Reservation audit is not bound to its actor and usage");

            async Task<FactoryPlan> Save() => JsonSerializer.Deserialize<FactoryPlan>(
                await Request(alice.Token, HttpMethod.Put, path, HttpStatusCode.OK, draft), Json.Options)!;
            Task<string> Adopt(Guid? revision, HttpStatusCode status, bool reviewed = true) =>
                Request(alice.Token, HttpMethod.Post, path + "/adopt", status,
                    new PlanAdoptInput { PlanRevision = revision!.Value, ReviewedExistingWork = reviewed });

            async Task<string> Request(string token, HttpMethod method, string url, HttpStatusCode status, object? body = null)
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
