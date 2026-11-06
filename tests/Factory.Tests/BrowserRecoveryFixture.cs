using Factory;

internal static class BrowserRecoveryFixture
{
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
