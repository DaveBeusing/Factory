using System.Diagnostics;
using System.Text;
using Factory;

internal static class DashboardStateTests
{
    internal static void Run()
    {
        using var fixture = new DashboardFixture();
        var now = DateTimeOffset.UtcNow;
        var project = new ProjectInfo { Name = "Large dashboard fixture" };
        var goal = new FactoryGoal { ProjectId = project.Id, Objective = "Dashboard metrics" };
        var job = new FactoryJob { GoalId = goal.Id, ProjectId = project.Id };
        var earlyBudget = new FactoryEvent { JobId = job.Id, Type = "budget_held", At = now };
        fixture.Store.Change(state =>
        {
            state.Projects.Add(project);
            state.Goals.Add(goal);
            state.Jobs.Add(job);
            state.Events.Add(earlyBudget);
            state.Events.AddRange(Enumerable.Range(0, 800).Select(i =>
                new FactoryEvent { At = now.AddSeconds(i), Type = "unrelated", Message = new string('x', 400) }));
            state.Knowledge.AddRange(Enumerable.Range(0, 800).Select(i =>
                new KnowledgeItem { ProjectId = project.Id, Topic = "Fixture " + i, Content = new string('y', 600) }));
        });

        using var watcher = fixture.Store.WatchDashboardState();
        var first = watcher.Current();
        var start = Stopwatch.GetTimestamp();
        var full = fixture.Store.Read();
        var fullBytes = Encoding.UTF8.GetByteCount(Json.Stringify(full));
        var fullElapsed = Stopwatch.GetElapsedTime(start);
        start = Stopwatch.GetTimestamp();
        var projection = fixture.Store.ReadDashboardState();
        var projectedBytes = Encoding.UTF8.GetByteCount(Json.Stringify(projection));
        var projectedElapsed = Stopwatch.GetElapsedTime(start);
        Require(projection.Events.Count == 250 && projection.Knowledge.Count == 0 &&
            projection.Jobs.Count == 1 && projection.Goals.Count == 1 && projection.Projects.Count == 1,
            "The projection did not retain only data used by the dashboard");
        Require(fixture.Store.ReadDashboardJobActivity(job.Id).Single().Id == earlyBudget.Id &&
            fixture.Store.ReadDashboardBudgetActivity(goal.Id).Single().Id == earlyBudget.Id,
            "Older job and budget history became unavailable");
        Require(full.Events.Count == 801 && full.Knowledge.Count == 800 && projectedBytes < fullBytes,
            "Full state compatibility or projection size regression");
        Require(first == watcher.Current(), "Read-only traffic invalidated an unchanged revision");

        var separateConnection = new StateStore(fixture.Store.Home);
        separateConnection.Event(job.Id, "stage", "Changed by another writer");
        var changed = watcher.Current();
        Require(changed != first && fixture.Store.ReadDashboardState().Events[^1].Type == "stage",
            "A separate writer's commit did not invalidate the projection");
        using var restarted = fixture.Store.WatchDashboardState();
        Require(changed != restarted.Current(), "A controller restart reused a prior validator");
        Console.WriteLine($"BENCHMARK large dashboard read + serialization: full {fullBytes} bytes / {fullElapsed.TotalMilliseconds:F1} ms, " +
            $"projected {projectedBytes} bytes / {projectedElapsed.TotalMilliseconds:F1} ms (single fixture read, no performance threshold)");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class DashboardFixture : IDisposable
    {
        private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("factory-dashboard-state-");
        public DashboardFixture() => Store = new StateStore(_home.FullName);
        public StateStore Store { get; }
        public void Dispose() => _home.Delete(recursive: true);
    }
}
