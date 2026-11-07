using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Factory;
using Microsoft.Data.Sqlite;

internal static class ProjectPlanningTests
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var home = Path.Combine(Path.GetTempPath(), "factory-planning-rights-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StateStore(home);
            var first = new ProjectInfo { Name = "First" };
            var second = new ProjectInfo { Name = "Second" };
            var foreign = new FactoryGoal { ProjectId = second.Id, Objective = "Foreign draft" };
            var active = new FactoryGoal { ProjectId = first.Id, Objective = "Already active", State = "active" };
            var local = new FactoryJob { ProjectId = first.Id, StartedAt = DateTimeOffset.UtcNow };
            var foreignJob = new FactoryJob { ProjectId = second.Id };
            var leased = new FactoryJob { ProjectId = first.Id, State = JobStates.Building, LeaseOwner = "remote:fixture" };
            store.Change(state =>
            {
                state.Projects.AddRange([first, second]);
                state.Goals.AddRange([foreign, active]);
                state.Jobs.AddRange([local, foreignJob, leased]);
            });
            var alice = store.CreateViewer("Alice");
            var bob = store.CreateViewer("Bob");
            store.SetViewerGrant(alice.SubjectId, first.Id, grant: true);
            store.SetViewerGrant(bob.SubjectId, first.Id, grant: true);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(home, "factory.sqlite3"), Pooling = false
            }.ToString());
            db.Open();
            Execute("""
                DROP TABLE planner_project_grants;
                DROP TABLE project_planning_audit;
                DROP TABLE operator_project_grants;
                DROP TABLE project_operation_audit;
                PRAGMA user_version=12;
                """);
            store = new StateStore(home);

            var settings = new FactorySettings();
            settings.EnsureCredentials();
            File.WriteAllText(Path.Combine(home, "settings.json"), Json.Stringify(settings));
            using var dashboard = await RemoteRestoreTests.DashboardProcess.StartAsync(
                home, "unused-executor-test-token", "http://127.0.0.1:0", timeout.Token);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(dashboard.Address)
            };
            var firstUrl = $"/api/viewer/projects/{first.Id}";
            var secondUrl = $"/api/viewer/projects/{second.Id}";
            var firstGoals = firstUrl + "/goals";
            var draft = new { objective = "New draft", tokenBudget = 1234, subjectId = bob.SubjectId };
            await Request(alice.Token, HttpMethod.Get, firstUrl, HttpStatusCode.OK);
            await Request(alice.Token, HttpMethod.Post, firstGoals, HttpStatusCode.Forbidden, draft);
            store.SetViewerGrant(alice.SubjectId, first.Id, grant: true, planner: true);
            store.SetViewerGrant(bob.SubjectId, second.Id, grant: true, planner: true);
            // The requests must finish while the fixture still owns the writer lock.
            using (db.BeginTransaction(deferred: false))
            using (var lockDeadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                lockDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                var unknownToken = "factory-viewer-" + new string('0', 64);
                await Request(unknownToken, HttpMethod.Post, firstGoals, HttpStatusCode.Unauthorized, draft, lockDeadline.Token);
                await Request(bob.Token, HttpMethod.Post, firstGoals, HttpStatusCode.Forbidden, draft, lockDeadline.Token);
                await Request(alice.Token, HttpMethod.Post, secondUrl + "/goals", HttpStatusCode.NotFound, draft, lockDeadline.Token);
                await Request(alice.Token, HttpMethod.Put, firstGoals + $"/{foreign.Id}", HttpStatusCode.NotFound, draft, lockDeadline.Token);
            }
            await Request(bob.Token, HttpMethod.Get, secondUrl, HttpStatusCode.OK);
            await Request(bob.Token, HttpMethod.Post, firstGoals, HttpStatusCode.Forbidden, draft);
            await Request(alice.Token, HttpMethod.Post, secondUrl + "/goals", HttpStatusCode.NotFound, draft);
            await Request(alice.Token, HttpMethod.Post, firstGoals, HttpStatusCode.NotFound,
                new { objective = "Mismatched project", projectId = second.Id });
            await Request(alice.Token, HttpMethod.Put, firstGoals + $"/{foreign.Id}", HttpStatusCode.NotFound, draft);
            await Request(alice.Token, HttpMethod.Put, firstGoals + $"/{active.Id}", HttpStatusCode.Conflict, draft);
            await Request(alice.Token, HttpMethod.Post, "/api/goals", HttpStatusCode.Unauthorized, draft);
            Require(ReadAuditCount() == 0, "Denied writes must not create audit rows");

            var created = await Request(alice.Token, HttpMethod.Post, firstGoals, HttpStatusCode.Created, draft);
            var goal = JsonSerializer.Deserialize<FactoryGoal>(created, Json.Options)!;
            Require(goal.ProjectId == first.Id && goal.State == "draft" && goal.TokenBudget == 1234, "Incorrect goal created");
            var goalUrl = firstGoals + $"/{goal.Id}";
            await Request(bob.Token, HttpMethod.Put, goalUrl, HttpStatusCode.Forbidden, draft);
            await Request(alice.Token, HttpMethod.Put, goalUrl, HttpStatusCode.BadRequest,
                new { objective = "Budget change", tokenBudget = 10 });
            await Request(alice.Token, HttpMethod.Put, goalUrl, HttpStatusCode.OK,
                new { objective = "Edited draft", acceptanceCriteria = new[] { "Document behavior" }, subjectId = bob.SubjectId });

            using (var audit = db.CreateCommand())
            {
                audit.CommandText = "SELECT subject_id, project_id, target_id, operation, at FROM project_planning_audit ORDER BY id;";
                using var row = audit.ExecuteReader();
                foreach (var operation in new[] { "goal_draft_created", "goal_draft_updated" })
                {
                    Require(row.Read() && Guid.Parse(row.GetString(0)) == alice.SubjectId &&
                        Guid.Parse(row.GetString(1)) == first.Id && Guid.Parse(row.GetString(2)) == goal.Id &&
                        row.GetString(3) == operation && DateTimeOffset.TryParse(row.GetString(4), out _),
                        "Audit must use the server-resolved actor and target");
                }
                Require(!row.Read(), "Unexpected audit rows");
            }

            Execute("""
                CREATE TRIGGER reject_planning_audit BEFORE INSERT ON project_planning_audit
                BEGIN SELECT RAISE(ABORT, 'PRIVATE-AUDIT-FAILURE'); END;
                """);
            var failure = await Request(alice.Token, HttpMethod.Put, goalUrl, HttpStatusCode.InternalServerError,
                new { objective = "Must roll back", acceptanceCriteria = new[] { "Must roll back" } });
            var persisted = store.Read().Goals.Single(item => item.Id == goal.Id);
            Require(persisted.Objective == "Edited draft" && persisted.AcceptanceCriteria.SequenceEqual(["Document behavior"]) &&
                persisted.TokenBudget == 1234 && ReadAuditCount() == 2, "Audit failure must roll back the goal update");
            Require(!failure.Contains("PRIVATE-AUDIT-FAILURE", StringComparison.Ordinal), "Internal audit error was exposed");

            store.SetViewerGrant(alice.SubjectId, first.Id, grant: false, planner: true);
            await Request(alice.Token, HttpMethod.Get, firstUrl, HttpStatusCode.OK);
            await Request(alice.Token, HttpMethod.Put, goalUrl, HttpStatusCode.Forbidden, draft);
            store.SetViewerCredential(alice.SubjectId, rotate: false);
            await Request(alice.Token, HttpMethod.Post, firstGoals, HttpStatusCode.Unauthorized, draft);

            var control = firstUrl + $"/jobs/{local.Id}/action";
            await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.Forbidden, new { action = "pause" });
            store.SetOperatorGrant(bob.SubjectId, first.Id, grant: true);
            await Request(bob.Token, HttpMethod.Get, firstUrl, HttpStatusCode.OK);
            await Request(bob.Token, HttpMethod.Post, firstGoals, HttpStatusCode.Forbidden, draft);
            await Request(bob.Token, HttpMethod.Post, secondUrl + $"/jobs/{foreignJob.Id}/action",
                HttpStatusCode.Forbidden, new { action = "pause" });
            await Request(bob.Token, HttpMethod.Post, firstUrl + $"/jobs/{foreignJob.Id}/action",
                HttpStatusCode.NotFound, new { action = "pause" });
            await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.NotFound,
                new { action = "stop", jobId = foreignJob.Id });
            await Request(bob.Token, HttpMethod.Post, firstUrl + $"/jobs/{leased.Id}/action",
                HttpStatusCode.Conflict, new { action = "stop" });
            await Request(bob.Token, HttpMethod.Post, $"/api/jobs/{local.Id}/action",
                HttpStatusCode.Unauthorized, new { action = "pause" });
            await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.OK, new { action = "pause", subjectId = alice.SubjectId });
            await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.OK, new { action = "stop" });
            await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.BadRequest, new { action = "resume", reviewed = true });
            await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.Conflict, new { action = "resume", reason = "Reviewed" });
            var resume = new { action = "resume", reviewed = true, reason = "Worktree inspected" };
            var usage = new ProviderUsageEntry { ProjectId = first.Id, JobId = local.Id, ProviderId = "codex", InputTokens = 30 };
            var reservation = new ProviderBudgetReservation
            {
                ProjectId = first.Id, UsageId = usage.Id, ProviderId = usage.ProviderId, Reserved = 512, Consumed = 30
            };
            store.Change(state =>
            {
                state.ProviderUsages.Add(usage);
                state.ProviderBudgetReservations.Add(reservation);
            });
            await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.Conflict, resume);
            store.Change(state => ProviderBudgetLedger.Hold(state,
                state.ProviderBudgetReservations.Single(item => item.Id == reservation.Id), "Unverified fixture usage"));
            await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.Conflict, resume);
            Require(store.Job(local.Id).State == JobStates.Stopped &&
                store.Read().ProviderBudgetReservations.Single(item => item.Id == reservation.Id).State == "held",
                "A rejected resume must preserve the stopped job and hold");
            ProviderBudgetReconciliation.Reconcile(store, reservation.Id, new()
            {
                FinalInputTokens = 30, Reviewed = true, Reason = "Verified provider receipt"
            });
            Execute("""
                CREATE TRIGGER reject_control_audit BEFORE INSERT ON project_operation_audit
                BEGIN SELECT RAISE(ABORT, 'PRIVATE-CONTROL-FAILURE'); END;
                """);
            var failedControl = await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.InternalServerError, resume);
            Require(store.Job(local.Id).State == JobStates.Stopped &&
                !failedControl.Contains("PRIVATE-CONTROL-FAILURE", StringComparison.Ordinal), "Control audit did not roll back safely");
            Execute("DROP TRIGGER reject_control_audit;");
            await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.OK, resume);
            Require(store.Job(local.Id).State == JobStates.Queued && store.Job(foreignJob.Id).State == JobStates.Queued &&
                store.Job(leased.Id).State == JobStates.Building, "Operator changed a foreign or leased job");
            store.SetOperatorGrant(bob.SubjectId, first.Id, grant: false);
            await Request(bob.Token, HttpMethod.Post, control, HttpStatusCode.Forbidden, new { action = "stop" });
            using (var controlAudit = db.CreateCommand())
            {
                controlAudit.CommandText = """
                    SELECT subject_id,project_id,job_id,operation,previous_state,resulting_state,reviewed,reason
                    FROM project_operation_audit ORDER BY id;
                    """;
                using var row = controlAudit.ExecuteReader();
                foreach (var (operation, before, after) in new[]
                {
                    ("pause", JobStates.Queued, JobStates.Paused), ("stop", JobStates.Paused, JobStates.Stopped),
                    ("resume", JobStates.Stopped, JobStates.Queued)
                })
                {
                    Require(row.Read() && Guid.Parse(row.GetString(0)) == bob.SubjectId &&
                        Guid.Parse(row.GetString(1)) == first.Id && Guid.Parse(row.GetString(2)) == local.Id &&
                        row.GetString(3) == operation && row.GetString(4) == before && row.GetString(5) == after &&
                        row.GetBoolean(6) == (operation == "resume") &&
                        row.GetString(7) == (operation == "resume" ? resume.reason : ""),
                        "Job control audit did not bind actor, state and review");
                }
                Require(!row.Read(), "Denied operations created job control audit rows");
            }

            async Task<string> Request(string token, HttpMethod method, string path, HttpStatusCode expected,
                object? body = null, CancellationToken? deadline = null)
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body is not null) request.Content = JsonContent.Create(body);
                var cancellation = deadline ?? timeout.Token;
                using var response = await client.SendAsync(request, cancellation);
                Require(response.StatusCode == expected, $"{method} {path}: expected {expected}, got {response.StatusCode}");
                return await response.Content.ReadAsStringAsync(cancellation);
            }

            void Execute(string sql)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            long ReadAuditCount()
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM project_planning_audit;";
                return (long)command.ExecuteScalar()!;
            }
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
