using Factory;

internal static class DashboardFollowupTests
{
    internal static async Task QueueRestartAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        bool failUnpause = true;
        var scheduler = new DashboardQueue(() =>
        {
            if (failUnpause) throw new IOException("Fixture persistence failure");
        }, async () =>
        {
            if (Interlocked.Increment(ref starts) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else restarted.TrySetResult();
        }, ex => restarted.TrySetException(ex));
        try
        {
            scheduler.Start(resume: false);
            throw new InvalidOperationException("Expected startup persistence failure");
        }
        catch (IOException) { }
        failUnpause = false;
        Require(scheduler.Start(resume: false), "Queue remained occupied after failed startup");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!scheduler.Start(resume: false), "Queue accepted a concurrent Run");
        Require(scheduler.Start(resume: true), "Queue rejected Resume");
        release.SetResult();
        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(starts == 2, "Queue failed to run exactly one replacement loop");
    }

    internal static void HealthProjection()
    {
        var home = Directory.CreateTempSubdirectory("factory-dashboard-health-");
        try
        {
            var store = new StateStore(home.FullName);
            var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
            var project = new ProjectInfo { Name = "fixture" };
            store.Change(s =>
            {
                s.Projects.Add(project);
                s.Projects.Add(new ProjectInfo { Name = "paused", Status = "paused" });
                s.Jobs.Add(new FactoryJob { ProjectId = project.Id, State = JobStates.Building, LeaseUntil = now.AddMinutes(1) });
                s.Jobs.Add(new FactoryJob { ProjectId = project.Id, State = JobStates.Reviewing, LeaseUntil = now.AddMinutes(-1) });
                s.Workers.Add(new WorkerInfo { Id = "online", Heartbeat = now.AddSeconds(-10).ToOffset(TimeSpan.FromHours(-6)) });
                s.Workers.Add(new WorkerInfo { Id = "offline", Heartbeat = now.AddSeconds(-20).ToOffset(TimeSpan.FromHours(6)) });
            });
            store.SetQueuePaused(true);
            store.Event(null, "fixture", "last");
            var health = store.ReadDashboardHealth(now);
            var snapshot = store.Read();
            Require(health == new DashboardHealth(now, true, 1, 1, 1, 2, snapshot.Events.Last().At),
                "Projected health changed the snapshot counts or latest event");
            Require(!store.IsWorkerDisabled("unknown"), "Unknown worker must be allowed to register");
            store.Change(s => s.Workers.Single(w => w.Id == "online").Enabled = false);
            Require(store.IsWorkerDisabled("online"), "Disabled worker must be identified");
        }
        finally { try { home.Delete(true); } catch (IOException) { } }
    }

    private static void Require(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
