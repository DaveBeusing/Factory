using Factory;

internal static class BrowserRecoveryFixture
{
    public static object SeedProjectActivity(string home)
    {
        var store = new StateStore(home);
        var first = new ProjectInfo { Name = "First activity project" };
        var second = new ProjectInfo { Name = "Second activity project" };
        var hidden = new ProjectInfo { Name = "Hidden activity project" };
        var firstJob = new FactoryJob { ProjectId = first.Id, Title = "First activity job" };
        var secondJob = new FactoryJob { ProjectId = second.Id, Title = "Second activity job" };
        var hiddenJob = new FactoryJob { ProjectId = hidden.Id, Title = "Hidden activity job" };
        store.Change(state =>
        {
            state.Projects.AddRange([first, second, hidden]);
            state.Jobs.AddRange([firstJob, secondJob, hiddenJob]);
        });
        store.Event(firstJob.Id, "queued", "private internal fixture message");
        store.Event(secondJob.Id, "queued", "another project event");
        store.Event(hiddenJob.Id, "queued", "hidden project event");
        store.Event(null, "queued", "global event without ownership");
        var viewer = store.CreateViewer("Browser activity viewer");
        store.SetViewerGrant(viewer.SubjectId, first.Id, grant: true);
        store.SetViewerGrant(viewer.SubjectId, second.Id, grant: true);
        return new { viewer = viewer.Token, viewer.SubjectId, firstId = first.Id, secondId = second.Id,
            hiddenId = hidden.Id, firstJobId = firstJob.Id, secondJobId = secondJob.Id };
    }

    public static object SeedProjectPlanning(string home, string repository)
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "Planner project", Path = repository };
        var other = new ProjectInfo { Name = "Viewer project" };
        var foreign = new FactoryGoal { ProjectId = other.Id, Objective = "Other project goal" };
        store.Change(state =>
        {
            state.Projects.AddRange([project, other]);
            state.Goals.Add(foreign);
        });
        var planner = store.CreateViewer("Browser planner");
        var viewer = store.CreateViewer("Browser planning viewer");
        store.SetViewerGrant(planner.SubjectId, project.Id, grant: true, planner: true);
        store.SetViewerGrant(planner.SubjectId, other.Id, grant: true);
        store.SetViewerGrant(viewer.SubjectId, project.Id, grant: true);
        return new { planner = planner.Token, viewer = viewer.Token, projectId = project.Id, otherId = other.Id, foreignGoalId = foreign.Id };
    }

    public static void SeedProviderEvidence(string directory, string executorUrl)
    {
        var store = new StateStore(directory);
        store.Change(state =>
        {
            var project = new ProjectInfo { Name = "Provider evidence fixture", Path = directory };
            var job = new FactoryJob { ProjectId = project.Id, Title = "Blocked provider fixture",
                Mode = JobExecutionModes.Remote, State = JobStates.Blocked, Stage = "reconcile",
                LeaseToken = "fixture-lease-secret", ExecutionGeneration = 5, TokensIn = 60, TokensOut = 30 };
            var lease = new RemoteProviderLease { JobId = job.Id, ExecutionGeneration = 4, WorkerId = "fixture",
                ExecutorUrl = executorUrl };
            var usage = new ProviderUsageEntry { JobId = job.Id, ProjectId = project.Id,
                ProviderId = "fixture-provider", Model = "requested-v1", Role = "builder", InputTokens = 60, OutputTokens = 30,
                ExecutionGeneration = lease.ExecutionGeneration };
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

    public static object SeedProjectRecovery(string home, string executorUrl)
    {
        SeedProviderEvidence(home, executorUrl);
        var store = new StateStore(home);
        var project = store.Read().Projects.Single();
        var other = new ProjectInfo { Name = "Read-only project" };
        var hidden = new ProjectInfo { Name = "Unrelated project" };
        var reconciler = store.CreateViewer("Browser reconciler");
        var operatorAccess = store.CreateViewer("Browser operator");
        store.Change(state =>
        {
            state.Projects.AddRange([other, hidden]);
            foreach (var owner in new[] { project, other, hidden })
            {
                var goal = new FactoryGoal { ProjectId = owner.Id, Objective = owner.Name + " goal", TokenBudget = 1000 };
                var job = new FactoryJob { ProjectId = owner.Id, GoalId = goal.Id, Title = owner.Name + " goal job",
                    State = JobStates.Stopped, ExecutionGeneration = 1, TokensIn = 20 };
                state.Goals.Add(goal);
                state.Jobs.Add(job);
                state.BudgetReservations.Add(new GoalBudgetReservation { GoalId = goal.Id, JobId = job.Id,
                    State = GoalBudgetLedger.Held, Reserved = 100, Consumed = 20, ExecutionGeneration = 1 });
                var usage = new ProviderUsageEntry { ProjectId = owner.Id, GoalId = goal.Id, JobId = job.Id,
                    ProviderId = owner.Id == project.Id ? "manual-provider" : "foreign-provider",
                    Model = "fixture-model", InputTokens = 20, ExecutionGeneration = 1 };
                state.ProviderUsages.Add(usage);
                state.ProviderBudgetReservations.Add(new ProviderBudgetReservation { ProjectId = owner.Id,
                    UsageId = usage.Id, ProviderId = usage.ProviderId, Reserved = 28, Consumed = 20, State = "held" });
            }
        });
        store.SetBudgetReconcilerGrant(reconciler.SubjectId, project.Id, grant: true);
        store.SetViewerGrant(reconciler.SubjectId, other.Id, grant: true);
        store.SetViewerGrant(operatorAccess.SubjectId, project.Id, grant: true, planner: true);
        store.SetOperatorGrant(operatorAccess.SubjectId, project.Id, grant: true);
        return new { reconciler = reconciler.Token, operatorToken = operatorAccess.Token, projectId = project.Id, otherId = other.Id };
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

    public static object SeedProjectJobs(string home, string worktree, string checkpoint)
    {
        var store = new StateStore(home);
        var project = new ProjectInfo { Name = "Operator project", Path = worktree };
        var other = new ProjectInfo { Name = "Viewer project" };
        var local = new FactoryJob { ProjectId = project.Id, Title = "Local work package", StartedAt = DateTimeOffset.UtcNow };
        var running = new FactoryJob { ProjectId = project.Id, Title = "Running local work package" };
        var remote = new FactoryJob { ProjectId = project.Id, Title = "Running remote work package", Mode = JobExecutionModes.Remote };
        var interrupted = new FactoryJob { ProjectId = project.Id, Title = "Interrupted local work package",
            Worktree = worktree, State = JobStates.Blocked, Stage = "reconcile", CheckpointStage = "agent", CheckpointCommit = checkpoint,
            WorkerProcessId = Environment.ProcessId, WorkerProcessStartedAt = DateTimeOffset.UnixEpoch,
            WorkerMachineName = Environment.MachineName };
        var foreign = new FactoryJob { ProjectId = other.Id, Title = "Foreign work package" };
        store.Change(state =>
        {
            state.Projects.AddRange([project, other]);
            state.Jobs.AddRange([local, running, remote, interrupted, foreign]);
            JobRecovery.Assign(running, "local-fixture", 300, local: true);
            JobRecovery.Assign(remote, "remote:fixture", 300, local: false);
        });
        var operatorAccess = store.CreateViewer("Browser project operator");
        var budgetAccess = store.CreateViewer("Browser budget reconciler");
        store.SetOperatorGrant(operatorAccess.SubjectId, project.Id, grant: true);
        store.SetViewerGrant(operatorAccess.SubjectId, other.Id, grant: true);
        store.SetBudgetReconcilerGrant(budgetAccess.SubjectId, project.Id, grant: true);
        return new { operatorToken = operatorAccess.Token, budgetToken = budgetAccess.Token, projectId = project.Id,
            otherId = other.Id, foreignJobId = foreign.Id, checkpoint };
    }
}
