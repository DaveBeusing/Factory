using Factory;

internal static class SpecificationRegressionTests
{
    internal static void PrerequisitesRequireCurrentTarget() => WithProject((store, project, goal, directory) =>
    {
        var head = new string('a', 40);
        var integrated = new string('b', 40);
        var predecessor = new FactoryJob
        {
            ProjectId = project.Id, State = JobStates.Integrated, Integrated = true, HeadSha = head
        };
        var proof = new IntegrationProof("fixture", GitOperations.Head(project.Path), integrated);
        store.Change(state => state.Jobs.Add(predecessor));
        RecordFixtureIntegration(store, predecessor.Id, head, proof);
        FactoryPlan Draft(Guid? revision = null, string? commit = null)
        {
            var plan = Plan(goal, revision);
            plan.Packages[0].IntegratedPrerequisites.Add(new(predecessor.Id, head, commit ?? integrated));
            return plan;
        }

        Reject(() => MissionControlManager.SavePlan(store, Draft(commit: new string('c', 40))), "declared prerequisite commits");
        var original = MissionControlManager.SavePlan(store, Draft());
        MoveCheckout(store, project, directory);
        Reject(() => MissionControlManager.SavePlan(store, Draft(original.Revision)), "Reverify the prerequisite");
        Reject(() => Planning.Adopt(store, goal.Id, "local", true, original.Revision!.Value), "Reverify the prerequisite");

        store.Change((state, db, tx) =>
        {
            StateStore.RecordIntegrationTarget(db, tx, state.Jobs.Single(), state.Projects.Single(), proof, state);
            return true;
        });
        var refreshed = MissionControlManager.SavePlan(store, Draft(original.Revision));
        var adopted = Planning.Adopt(store, goal.Id, "local", true, refreshed.Revision!.Value);
        if (!adopted.Single(job => job.Title == "Predecessor").DependsOn.SequenceEqual([predecessor.Id]))
            throw new InvalidOperationException("The adopted package lost its verified prerequisite.");
    });

    internal static void RetainedPredecessorsRequireCurrentTarget() => WithProject((store, project, goal, directory) =>
    {
        var plan = MissionControlManager.SavePlan(store, Plan(goal));
        var initial = Planning.Adopt(store, goal.Id, "local", true, plan.Revision!.Value);
        var head = new string('a', 40);
        var proof = new IntegrationProof("fixture", GitOperations.Head(project.Path), new string('b', 40));
        RecordFixtureIntegration(store, initial[0].Id, head, proof);
        var amended = MissionControlManager.SavePlan(store, Plan(goal, plan.Revision));
        MoveCheckout(store, project, directory);
        Reject(() => Planning.Adopt(store, goal.Id, "local", true, amended.Revision!.Value), "Reverify the retained predecessor");
        RecordFixtureIntegration(store, initial[0].Id, head, proof);
        var adopted = Planning.Adopt(store, goal.Id, "local", true, amended.Revision!.Value);
        if (!adopted.Single().DependsOn.SequenceEqual([initial[0].Id]))
            throw new InvalidOperationException("The replacement lost its retained predecessor.");
    });

    internal static void ReplaceTerminalJobs() => WithProject((store, _, goal, _) =>
    {
        var plan = MissionControlManager.SavePlan(store, Plan(goal));
        var jobs = Planning.Adopt(store, goal.Id, "local", true, plan.Revision!.Value);
        var predecessor = jobs[0];

        store.UpdateJob(predecessor.Id, job =>
        {
            job.Attempt = 1;
            job.State = JobStates.Blocked;
            job.Stage = "interrupted_git";
        });
        Reject(() => MissionControlManager.SavePlan(store, Plan(goal, plan.Revision)), "Existing jobs prevent");
        var blocked = store.Job(predecessor.Id);
        JobRecovery.ReleaseResources(store, blocked.Id, new(blocked.ControlRevision, true, true, "Reviewed previous Git effects."));
        var replacement = MissionControlManager.SavePlan(store, Plan(goal, plan.Revision));
        jobs = Planning.Adopt(store, goal.Id, "local", true, replacement.Revision!.Value);
        VerifyReplacement(store, predecessor.Id, jobs);

        predecessor = jobs[0];
        store.UpdateJob(predecessor.Id, job =>
        {
            job.Attempt = 1;
            job.State = JobStates.Blocked;
            job.Stage = "quality";
        });
        var hold = new GoalBudgetReservation
        {
            JobId = predecessor.Id, GoalId = goal.Id, State = GoalBudgetLedger.Held, Reserved = 100
        };
        store.Change(state => state.BudgetReservations.Add(hold));
        var reviewed = MissionControlManager.SavePlan(store, Plan(goal, replacement.Revision));
        Reject(() => Planning.Adopt(store, goal.Id, "local", true, reviewed.Revision!.Value), "Settle the previous job's goal budget");
        if (store.Job(predecessor.Id).State != JobStates.Blocked)
            throw new InvalidOperationException("A failed adoption changed the previous work package.");

        store.Change(state => state.BudgetReservations.Single().State = GoalBudgetLedger.Released);
        jobs = Planning.Adopt(store, goal.Id, "local", true, reviewed.Revision!.Value);
        VerifyReplacement(store, predecessor.Id, jobs);
    });

    private static FactoryPlan Plan(FactoryGoal goal, Guid? revision = null) => new()
    {
        GoalId = goal.Id,
        ProjectId = goal.ProjectId,
        ContractVersion = 1,
        Revision = revision,
        GoalRevision = Planning.GoalRevision(goal),
        Packages =
        [
            new() { Title = "Predecessor", Description = "Prepare behavior", Files = ["first.txt"], AcceptanceEvidence = [new("Verified", "fixture")] },
            new() { Title = "Dependent", Description = "Use the result", Files = ["later.txt"], DependsOn = [0], AcceptanceEvidence = [new("Verified", "fixture")] }
        ]
    };

    private static void VerifyReplacement(StateStore store, Guid retiredJobId, IReadOnlyList<FactoryJob> jobs)
    {
        if (store.Job(retiredJobId).Stage != "specification_superseded" ||
            jobs.Count != 2 || !jobs[1].DependsOn.SequenceEqual([jobs[0].Id]))
            throw new InvalidOperationException("Replacement must retire the predecessor and wire the dependent to the new job.");
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"Expected rejection containing '{message}'.");
    }

    private static void RecordFixtureIntegration(StateStore store, Guid jobId, string head, IntegrationProof proof)
    {
        store.Change((state, db, tx) =>
        {
            var job = state.Jobs.Single(candidate => candidate.Id == jobId);
            StateStore.RecordLifecycle(db, tx, job.ProjectId, "job", job.Id, "fixture",
                job.State, JobStates.Integrated, LifecycleReason.IntegrationVerified, reference: proof.IntegratedCommit);
            job.HeadSha = head;
            job.State = JobStates.Integrated;
            job.Integrated = true;
            StateStore.RecordIntegratedHead(db, tx, job, proof);
            StateStore.RecordIntegrationTarget(db, tx, job, state.Projects.Single(), proof, state);
            return true;
        });
    }

    private static void MoveCheckout(StateStore store, ProjectInfo project, string directory)
    {
        var movedCheckout = Path.Combine(directory, "moved");
        Directory.Move(project.Path, movedCheckout);
        store.Change(state => state.Projects.Single().Path = movedCheckout);
    }

    private static void WithProject(Action<StateStore, ProjectInfo, FactoryGoal, string> test)
    {
        var root = Directory.CreateTempSubdirectory("factory-specification-");
        try
        {
            var repo = root.CreateSubdirectory("repo").FullName;
            GitOperations.Git(repo, "init", "-b", "master");
            GitOperations.Git(repo, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
                "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Fixture");
            var store = new StateStore(Path.Combine(root.FullName, "state"));
            var project = new ProjectInfo
            {
                Name = "Specification regression", Path = repo,
                Checks = [new() { Name = "fixture", Executable = "git", Arguments = ["--version"] }]
            };
            var goal = new FactoryGoal { ProjectId = project.Id, Objective = "Verify the revised contract" };
            store.Change(state => { state.Projects.Add(project); state.Goals.Add(goal); });
            test(store, project, goal, root.FullName);
        }
        finally { try { root.Delete(true); } catch { } }
    }
}
