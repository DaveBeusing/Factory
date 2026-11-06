using Factory;

internal static class BrowserRecoveryFixture
{
    public static void SeedProviderEvidence(string directory, string executorUrl)
    {
        var store = new StateStore(directory);
        store.Change(state =>
        {
            var project = new ProjectInfo { Name = "Provider evidence fixture", Path = directory };
            var job = new FactoryJob { ProjectId = project.Id, Title = "Blocked provider fixture",
                Mode = JobExecutionModes.Remote, State = JobStates.Blocked, Stage = "reconcile",
                LeaseToken = "fixture-lease-secret" };
            var lease = new RemoteProviderLease { JobId = job.Id, ExecutionGeneration = 4, WorkerId = "fixture",
                ExecutorUrl = executorUrl };
            var usage = new ProviderUsageEntry { JobId = job.Id, ProjectId = project.Id,
                ProviderId = "fixture-provider", Model = "requested-v1", Role = "builder", InputTokens = 60, OutputTokens = 30 };
            state.Projects.Add(project);
            state.Jobs.Add(job);
            state.RemoteProviderLeases.Add(lease);
            state.ProviderUsages.Add(usage);
            state.ProviderBudgetReservations.Add(new ProviderBudgetReservation
            {
                UsageId = usage.Id, ProjectId = project.Id, ProviderId = usage.ProviderId,
                Reserved = 512, Consumed = 90, State = "held"
            });
            state.RemoteProviderTurns.Add(new RemoteProviderTurn { Id = Guid.NewGuid(), LeaseId = lease.Id,
                UsageId = usage.Id, Role = usage.Role, State = "held",
                HoldReason = "Executor response was interrupted", GrantDigest = new string('A', 64) });
        });
        File.WriteAllText(Path.Combine(directory, "settings.json"), Json.Stringify(new FactorySettings
        {
            RemoteProviderExecutorUrl = executorUrl
        }));
    }

    public static void Seed(string home)
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "Recovery fixture", Path = home };
        var goal = new FactoryGoal { ProjectId = project.Id, Objective = "Recover interrupted fixture", TokenBudget = 1500, State = "active" };
        var job = new FactoryJob { ProjectId = project.Id, GoalId = goal.Id, Title = "Interrupted work package" };
        var provider = new ProviderProfile { Id = "codex", TokenLimit = 3000 };
        store.Change(state =>
        {
            state.Projects.Add(project);
            state.Goals.Add(goal);
            state.Jobs.Add(job);
            JobRecovery.Assign(job, "source-controller", 120, local: true, state: state, reservationTokens: 600);
            var turn = ProviderBudgetLedger.Begin(state, job, provider, "builder", "", 512);
            ProviderBudgetLedger.Record(state, turn.Id, new(100, 20));
            GoalBudgetLedger.Record(state, job, 100, 20);
            state.ProviderBudgetReservations.Single().OwnerMachine = "retired-browser-fixture-host";
            job.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
        });
        JobRecovery.QuarantineExpired(store);
    }
}
