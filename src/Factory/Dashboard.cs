using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Factory;

public static class DashboardHost
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);
    public static async Task RunAsync(StateStore store, FactorySettings settings, string listenUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiToken))
            throw new InvalidOperationException("API token missing; run 'factory doctor' to initialize settings");
        var uri = new Uri(listenUrl);
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new InvalidOperationException("Non-loopback API endpoint requires HTTPS with a configured certificate");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls(listenUrl);
        var app = builder.Build();

        static IResult Asset(string resource, string contentType)
        {
            var stream = typeof(DashboardHost).Assembly.GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException("Mission Control asset missing: " + resource);
            return Results.Stream(stream, contentType, enableRangeProcessing: false);
        }
        app.MapGet("/", () => Asset("Factory.Ui.Index", "text/html; charset=utf-8"));
        app.MapGet("/ui/app.css", () => Asset("Factory.Ui.Style", "text/css; charset=utf-8"));
        app.MapGet("/ui/app.js", () => Asset("Factory.Ui.Script", "text/javascript; charset=utf-8"));
        app.MapGet("/readyz", () => Results.Ok(new { ready = true, at = DateTimeOffset.UtcNow }));
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; font-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'";
            if (ctx.Request.Path.StartsWithSegments("/api") &&
                !WorkerProtocol.Authorized(ctx.Request.Headers.Authorization.ToString(), settings.ApiToken))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return;
            }
            try { await next(); }
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = ex is InvalidOperationException or ArgumentException or FileNotFoundException
                    ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });
        app.MapGet("/api/state", () => Results.Ok(store.Read()));
        app.MapGet("/api/usage", async (CancellationToken ct) => Results.Ok(await UsageMonitor.GetAsync(store.Home, ct)));
        app.MapGet("/api/health", () => Results.Ok(Health(store)));
        app.MapGet("/api/workers/managed", () => Results.Ok(ManagedWorkers.RunningIds()));
        app.MapGet("/api/events/stream", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                var json = Json.Stringify(Health(store)).Replace("\r", "").Replace("\n", "");
                await ctx.Response.WriteAsync("event: heartbeat\ndata: " + json + "\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                await Task.Delay(TimeSpan.FromSeconds(3), ctx.RequestAborted);
            }
        });
        app.MapGet("/api/settings", () => Results.Ok(new
        {
            settings.MaxParallel, settings.MaxAgentSessions, settings.MaxReviewRounds,
            settings.LeaseSeconds, settings.PollSeconds, settings.AllowPush, settings.AutoMerge, settings.Runtime
        }));
        app.MapPut("/api/settings", (SettingsInput input) =>
        {
            if (input.MaxParallel is < 1 or > 32 || input.MaxAgentSessions is < 1 or > 64 ||
                input.MaxReviewRounds is < 1 or > 10 || input.LeaseSeconds is < 30 or > 3600 ||
                input.PollSeconds is < 1 or > 300)
                return (IResult)Results.BadRequest(new { error = "Settings out of range" });
            if (input.AutoMerge)
                return (IResult)Results.BadRequest(new { error = "Automatic merging is not supported; merge through a manually approved PR" });
            settings.MaxParallel = input.MaxParallel;
            settings.MaxAgentSessions = input.MaxAgentSessions;
            settings.MaxReviewRounds = input.MaxReviewRounds;
            settings.LeaseSeconds = input.LeaseSeconds;
            settings.PollSeconds = input.PollSeconds;
            settings.AllowPush = input.AllowPush;
            settings.AutoMerge = false;
            var destination = Path.Combine(store.Home, "settings.json");
            var temporary = destination + ".tmp";
            File.WriteAllText(temporary, Json.Stringify(settings));
            File.Move(temporary, destination, true);
            store.Event(null, "settings_updated", "Execution settings updated through Mission Control");
            return Results.Ok(new { settings.MaxParallel, settings.MaxAgentSessions, settings.MaxReviewRounds,
                settings.LeaseSeconds, settings.PollSeconds, settings.AllowPush, settings.AutoMerge });
        });
        app.MapGet("/api/plans", () => Results.Ok(store.Read().Goals.Select(g =>
            new { g.Id, g.ProjectId, g.State, g.Objective, Plan = MissionControlManager.ReadPlan(store, g.Id) })));
        app.MapGet("/api/plans/{goalId:guid}", (Guid goalId) =>
            MissionControlManager.ReadPlan(store, goalId) is { } plan ? (IResult)Results.Ok(plan) : Results.NotFound());
        app.MapPut("/api/plans/{goalId:guid}", (Guid goalId, FactoryPlan plan) =>
        {
            if (plan.GoalId != goalId) return (IResult)Results.BadRequest(new { error = "Plan goal ID mismatch" });
            return Results.Ok(MissionControlManager.SavePlan(store, plan));
        });
        app.MapPost("/api/plans/{goalId:guid}/generate", (Guid goalId) =>
        {
            var state = store.Read();
            var goal = state.Goals.Single(g => g.Id == goalId);
            if (goal.State != "draft") return (IResult)Results.Conflict(new { error = "Only draft goals can generate plans" });
            var project = state.Projects.Single(p => p.Id == goal.ProjectId);
            _ = Task.Run(async () =>
            {
                store.Event(null, "plan_generating", goalId.ToString());
                try
                {
                    await Planning.ProposeAsync(store, project, goal, AgentRuntimeFactory.Create(settings), CancellationToken.None);
                    store.Event(null, "plan_generated", goalId.ToString());
                }
                catch (Exception ex) { store.Event(null, "plan_error", goalId + ": " + ex.Message); }
            });
            return Results.Accepted();
        });
        app.MapPost("/api/plans/{goalId:guid}/adopt", (Guid goalId, PlanAdoptInput input) =>
            Results.Ok(Planning.Adopt(store, goalId, input.Mode)));

        app.MapPost("/api/projects", (ProjectInput input) => Results.Created("/api/state", MissionControlManager.CreateProject(store, input)));
        app.MapPut("/api/projects/{id:guid}", (Guid id, ProjectInput input) => Results.Ok(MissionControlManager.UpdateProject(store, id, input)));
        app.MapPost("/api/projects/{id:guid}/action", (Guid id, ControlInput input) =>
            Results.Ok(MissionControlManager.SetProjectStatus(store, id, input.Action)));
        app.MapPost("/api/goals", (GoalInput input) => Results.Created("/api/state", MissionControlManager.CreateGoal(store, input)));
        app.MapPut("/api/goals/{id:guid}", (Guid id, GoalInput input) => Results.Ok(MissionControlManager.UpdateGoal(store, id, input)));
        app.MapPost("/api/goals/{id:guid}/action", (Guid id, ControlInput input) =>
            Results.Ok(MissionControlManager.SetGoalStatus(store, id, input.Action)));
        app.MapPost("/api/jobs", (JobInput input) => Results.Created("/api/state", MissionControlManager.CreateJob(store, input, settings)));
        app.MapPut("/api/jobs/{id:guid}", (Guid id, JobInput input) => Results.Ok(MissionControlManager.UpdateJob(store, id, input)));
        app.MapPost("/api/jobs/{id:guid}/action", (Guid id, ControlInput input) =>
            Results.Ok(MissionControlManager.ControlJob(store, id, input)));
        app.MapPost("/api/jobs/{id:guid}/start", (Guid id) =>
        {
            var job = store.Job(id);
            if (job.State != JobStates.Queued || job.Mode != "local")
                return (IResult)Results.Conflict(new { error = "Only queued local jobs can be launched directly" });
            if (!JobScheduler.Select(store.Read(), settings.MaxParallel).Any(x => x.Id == id))
                return Results.Conflict(new { error = "Job not eligible: review scheduling capacity, dependencies and conflicts" });
            _ = Task.Run(async () =>
            {
                try { await new FactoryEngine(store, settings).RunOneAsync(id, CancellationToken.None); }
                catch (Exception ex) { store.Event(id, "job_start_error", ex.Message); }
            });
            return Results.Accepted();
        });
        app.MapPost("/api/jobs/{id:guid}/verify", (Guid id) =>
        {
            new FactoryEngine(store, settings).VerifyIntegration(id);
            return Results.Ok();
        });
        app.MapPost("/api/workers/register", (WorkerInput input) => Results.Ok(MissionControlManager.RegisterWorker(store, input)));
        app.MapPost("/api/workers/{id}/action", (string id, ControlInput input) =>
        {
            if (input.Action == "start") { ManagedWorkers.Start(store, settings, listenUrl, id); return (IResult)Results.Accepted(); }
            if (input.Action == "stop") { ManagedWorkers.Stop(store, id); return (IResult)Results.Ok(); }
            return Results.Ok(MissionControlManager.ControlWorker(store, id, input.Action));
        });

        IResult LaunchQueue()
        {
            if (!RunLock.Wait(0)) return Results.Conflict(new { error = "Queue already running" });
            store.Change(s => s.Paused = false);
            _ = Task.Run(async () =>
            {
                try
                {
                    var plugins = FactoryPlugins.Load(Path.Combine(store.Home, "plugins.json"));
                    await new FactoryEngine(store, settings, plugins).RunQueueAsync(CancellationToken.None);
                }
                catch (Exception ex) { store.Event(null, "queue_error", ex.Message); }
                finally { RunLock.Release(); }
            });
            return Results.Accepted();
        }
        app.MapPost("/api/queue/run", LaunchQueue);
        app.MapPost("/api/queue/pause", () => { store.Change(s => s.Paused = true); return Results.Ok(); });
        app.MapPost("/api/queue/resume", () =>
        {
            store.Change(s => s.Paused = false);
            if (RunLock.CurrentCount > 0) return LaunchQueue();
            return (IResult)Results.Ok(new { resumed = true, scheduler = "running" });
        });
        app.MapPost("/api/queue/stop", () =>
        {
            store.Change(s => s.Paused = true);
            var jobs = store.Read().Jobs.Where(j => JobStates.Active.Contains(j.State) &&
                !j.LeaseOwner.StartsWith("remote:", StringComparison.Ordinal)).ToArray();
            var requested = new List<Guid>();
            foreach (var j in jobs)
            {
                try { MissionControlManager.ControlJob(store, j.Id, new ControlInput { Action = "stop", Reason = "Queue stop" }); requested.Add(j.Id); }
                catch (InvalidOperationException) { }
            }
            return Results.Ok(new { paused = true, cancellationRequested = requested,
                note = "Remote jobs cannot be force-stopped. Disable/drain remote workers." });
        });
        app.MapPost("/api/queue/recover", () => Results.Ok(new FactoryEngine(store, settings).Recover()));
        app.MapPost("/api/workers/claim", (WorkerClaimRequest req) =>
        {
            var claim = WorkerProtocol.Claim(store, settings, req.WorkerId);
            return claim is null ? (IResult)Results.NoContent() : Results.Ok(claim);
        });
        app.MapPost("/api/workers/heartbeat", (WorkerStatus req) =>
            WorkerProtocol.Heartbeat(store, req, settings) ? (IResult)Results.Ok() : Results.Conflict());
        app.MapPost("/api/workers/complete", (WorkerStatus req) =>
        {
            if (req.Evidence.Length > 1_000_000) return (IResult)Results.BadRequest(new { error = "Evidence too large" });
            if (req.Job is { State: JobStates.PrReady } &&
                !System.Text.RegularExpressions.Regex.IsMatch(req.Job.HeadSha, "^[0-9a-fA-F]{40,64}$"))
                return (IResult)Results.BadRequest(new { error = "Invalid commit hash" });
            if (!WorkerProtocol.Complete(store, req)) return (IResult)Results.Conflict();
            if (req.Evidence.Length > 0)
            {
                var job = store.Job(req.JobId);
                if (!System.Text.RegularExpressions.Regex.IsMatch(job.HeadSha, "^[0-9a-fA-F]{40,64}$"))
                    return (IResult)Results.BadRequest(new { error = "Invalid commit hash" });
                var dir = Path.Combine(store.Home, "evidence", req.JobId.ToString("N"));
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, job.HeadSha + ".json");
                File.WriteAllText(path, req.Evidence);
                store.UpdateJob(req.JobId, j => j.EvidenceFile = path);
            }
            return Results.Ok();
        });
        await app.RunAsync(cancellationToken);
    }

    private static object Health(StateStore store)
    {
        var s = store.Read();
        var now = DateTimeOffset.UtcNow;
        return new
        {
            serverTime = now,
            queuePaused = s.Paused,
            projects = s.Projects.Count(p => p.Status == "active"),
            activeJobs = s.Jobs.Count(j => JobStates.Active.Contains(j.State) && j.LeaseUntil > now),
            workersOnline = s.Workers.Count(w => (now - w.Heartbeat).TotalSeconds < 20),
            workersTotal = s.Workers.Count,
            lastEventAt = s.Events.LastOrDefault()?.At
        };
    }
}

public sealed class PlanAdoptInput
{
    public string Mode { get; set; } = "local";
}

public sealed class SettingsInput
{
    public int MaxParallel { get; set; } = 3;
    public int MaxAgentSessions { get; set; } = 6;
    public int MaxReviewRounds { get; set; } = 3;
    public int LeaseSeconds { get; set; } = 120;
    public int PollSeconds { get; set; } = 3;
    public bool AllowPush { get; set; }
    public bool AutoMerge { get; set; }
}
