using Factory;
using Microsoft.Data.Sqlite;

internal static class AuditPerformanceTests
{
    internal static void WorkflowDeliveryUpgrade()
    {
        var home = Directory.CreateTempSubdirectory("factory-delivery-upgrade-");
        try
        {
            var first = new ProjectInfo { Origin = "https://github.com/example/repo" };
            var second = new ProjectInfo { Origin = "https://github.com/Example/Repo" };
            var receipt = new WorkflowEventProof(new WorkflowEvent("example/repo", 42, 123, 1, 75, 91,
                first.BaseBranch, new string('a', 40), new string('b', 40)), "first-signature");
            var nextAttempt = new WorkflowEventProof(receipt.Event with { Attempt = 2 }, "other-signature");
            var store = new StateStore(home.FullName);
            store.Change(state => state.Projects.AddRange([first, second]));
            store.Change((state, db, tx) =>
            {
                StateStore.RecordLifecycle(db, tx, first.Id, "workflow-delivery", first.Id, "fixture", "received",
                    "authenticated", LifecycleReason.ApprovalGranted, reference: Json.Stringify(receipt));
                StateStore.RecordLifecycle(db, tx, second.Id, "workflow-delivery", second.Id, "fixture", "received",
                    "authenticated", LifecycleReason.ApprovalGranted, reference: "{");
                return true;
            });
            using (var db = Open(home.FullName))
            using (var command = db.CreateCommand())
            {
                command.CommandText = "DROP TABLE workflow_deliveries; DROP INDEX projects_workflow_base; PRAGMA user_version=25;";
                command.ExecuteNonQuery();
            }

            store = new StateStore(home.FullName);
            Check(store.FindWorkflowDelivery(receipt.Event) == receipt, "Retained receipt was lost during upgrade.");
            store.RecordWorkflowDelivery(receipt);
            store.RecordWorkflowDelivery(receipt with { Signature = "replay-signature" });
            store.RecordWorkflowDelivery(nextAttempt);
            Check(store.FindWorkflowDelivery(receipt.Event) == receipt, "A duplicate replaced the original receipt.");
            Check(store.FindWorkflowDelivery(nextAttempt.Event) == nextAttempt, "A distinct attempt did not resolve independently.");
            Check(store.FindWorkflowDelivery(receipt.Event with { SuiteId = 76 }) is null, "Lookup accepted a different identity.");

            using var reopened = Open(home.FullName);
            using var read = reopened.CreateCommand();
            read.CommandText = "SELECT count(*) FROM workflow_deliveries;";
            Check((long)read.ExecuteScalar()! == 4, "Delivery replay was indexed again for a previously matched project.");
            read.CommandText = "SELECT count(*) FROM lifecycle_audit WHERE entity_kind='workflow-delivery';";
            Check((long)read.ExecuteScalar()! == 5, "Delivery audit was lost or duplicated.");
        }
        finally { home.Delete(true); }
    }

    internal static void SchedulerAvailabilityAndBlockerOrdering()
    {
        var project = new ProjectInfo { MaxParallel = 5 };
        var other = new ProjectInfo();
        var goal = new FactoryGoal { ProjectId = project.Id, State = "active", TokenBudget = 1_500 };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, ExpectedPaths = ["src/"], Priority = 90 };
        var sibling = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, ExpectedPaths = ["docs/"], Priority = 20 };
        var historical = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, State = JobStates.Integrated,
            Integrated = true, TokensIn = 100 };
        var deferred = new FactoryJob { ProjectId = project.Id, ExpectedPaths = ["test/"], DependsOn = [historical.Id] };
        var conflict = new FactoryJob { ProjectId = project.Id, ExpectedPaths = ["src/code.cs"],
            State = JobStates.Blocked, Stage = "interrupted_git", Id = new Guid("01000000-0000-0000-0000-000000000000") };
        var secondConflict = new FactoryJob { ProjectId = project.Id, ExpectedPaths = ["src/"], State = JobStates.Blocked,
            Stage = "reconcile", Id = new Guid("02000000-0000-0000-0000-000000000000") };
        var foreign = new FactoryJob { ProjectId = other.Id, ExpectedPaths = ["src/"], State = JobStates.Blocked,
            Stage = "reconcile", Id = Guid.Empty };
        var plan = new ProviderUsageEntry { GoalId = goal.Id, InputTokens = 200, OutputTokens = 50 };
        var state = new FactoryState
        {
            Projects = [project, other], Goals = [goal],
            Jobs = [secondConflict, foreign, sibling, job, conflict, historical, deferred],
            ProviderUsages = [plan],
            ProviderBudgetReservations = [new() { UsageId = plan.Id, Reserved = 800, Consumed = 100, State = "held" }]
        };
        var available = GoalBudgetLedger.CreateSchedulerAvailability(state);
        Check(available(job) == 450 && available(job) == GoalBudgetLedger.Available(state, goal.Id),
            "Indexed goal availability omitted a planning liability.");
        var decisions = new List<SchedulingDecision>();
        var chosen = JobScheduler.SelectWithReasons(state, 5, JobExecutionModes.Local, null, 600, decisions);
        Check(chosen.Select(j => j.Id).SequenceEqual([sibling.Id]), "Unexpected scheduler selection.");
        Check(decisions.Contains(new SchedulingDecision(job.Id, "ownership_conflict", conflict.Id)),
            "The ordered project blocker was not preserved.");
        Check(decisions.Contains(new SchedulingDecision(deferred.Id, "dependency_not_integrated")),
            "Historical dependency was treated as current.");

        var legacy = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, State = JobStates.Building,
            LeaseOwner = "legacy", ExecutionGeneration = 4, ExpectedPaths = ["other/"] };
        state.Jobs.Add(legacy);
        Check(GoalBudgetLedger.CreateSchedulerAvailability(state)(job) == 0,
            "A legacy liability allowed reservation of budget.");
        state.BudgetReservations.Add(new() { JobId = legacy.Id, GoalId = goal.Id, ExecutionGeneration = 4, Reserved = 100 });
        Check(GoalBudgetLedger.CreateSchedulerAvailability(state)(job) == 350,
            "A current reservation did not restore bounded liability.");
    }

    private static SqliteConnection Open(string home)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(home, "factory.sqlite3"), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
