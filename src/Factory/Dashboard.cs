using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Factory;

public static class DashboardHost
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);
    public static async Task RunAsync(StateStore store, FactorySettings settings, string listenUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiToken) ||
            !WorkerAuthentication.IsValidKey(settings.WorkerTokenKey) ||
            string.Equals(settings.ApiToken, settings.WorkerTokenKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Controller credentials are missing or insecure; run 'factory doctor'");
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
        static IResult Index()
        {
            using var stream = typeof(DashboardHost).Assembly.GetManifestResourceStream("Factory.Ui.Index")
                ?? throw new FileNotFoundException("Mission Control asset missing: Factory.Ui.Index");
            using var reader = new StreamReader(stream);
            var information = typeof(DashboardHost).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = string.IsNullOrWhiteSpace(information)
                ? "Version unavailable" : "v" + information.Split('+')[0];
            var html = reader.ReadToEnd().Replace("{{FACTORY_VERSION}}",
                System.Net.WebUtility.HtmlEncode(version), StringComparison.Ordinal);
            return Results.Content(html, "text/html; charset=utf-8");
        }
        app.MapGet("/", Index);
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
            var isViewerRequest = ctx.GetEndpoint()?.Metadata.GetMetadata<ProjectViewerEndpoint>() is not null;
            if (ctx.Request.Path.StartsWithSegments("/api") && !isViewerRequest)
            {
                var path = ctx.Request.Path.Value;
                var workerId = ctx.Request.Headers[WorkerAuthentication.WorkerIdHeader].ToString();
                if (!WorkerAuthentication.Authorized(ctx.Request.Method, path,
                    ctx.Request.Headers.Authorization.ToString(), workerId, settings))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return;
                }
                // Disabled workers cannot register, claim jobs or submit results.
                if (WorkerAuthentication.IsWorkerNamespace(path) &&
                    store.Read().Workers.Any(w => w.Id == workerId && !w.Enabled))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return;
                }
            }
            try { await next(); }
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                if (isViewerRequest)
                {
                    app.Logger.LogError(ex, "Project viewer request failed");
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    var error = HttpMethods.IsGet(ctx.Request.Method)
                        ? "Unable to read project data." : "Unable to save project data.";
                    await ctx.Response.WriteAsJsonAsync(new { error });
                    return;
                }
                ctx.Response.StatusCode = ex is InvalidOperationException or ArgumentException or FileNotFoundException
                    ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });
        ProjectViewerEndpoint.Map(app, store, settings);
        app.MapGet("/api/state", () =>
        {
            var snapshot = store.Read();
            foreach (var job in snapshot.Jobs) job.LeaseToken = "";
            foreach (var reservation in snapshot.BudgetReservations) reservation.LeaseToken = "";
            foreach (var turn in snapshot.RemoteProviderTurns) turn.GrantDigest = "";
            return Results.Ok(snapshot);
        });
        app.MapGet("/api/usage", async (CancellationToken ct) => Results.Ok(await UsageMonitor.GetAsync(store.Home, ct)));
        app.MapGet("/api/providers", () =>
        {
            var snapshot = store.Read();
            return Results.Ok(new
            {
                profiles = settings.Providers.Select(p => new
                {
                    p.Id, p.Kind, p.Model, p.Enabled, p.TokenLimit,
                    capabilities = p.Kind switch
                    {
                        "codex" => new[] { "read-only", "workspace-write", "token-usage", "session" },
                        "claude" => new[] { "read-only", "token-usage", "reported-cost" },
                        _ => new[] { "read-only", "text-only", "token-usage" }
                    }
                }),
                assignments = snapshot.ProviderAssignments,
                budgets = snapshot.ProviderBudgetLimits,
                heldReservations = snapshot.ProviderBudgetReservations.Where(x => x.State == "held")
                    .Join(snapshot.ProviderUsages, r => r.UsageId, u => u.Id, (r, u) => new
                    {
                        r.Id, r.ProviderId, r.ProjectId, r.Reserved, r.Consumed,
                        goalReservationId = GoalBudgetLedger.LinkedReservation(snapshot, u)?.Id,
                        usage = new { u.Id, u.JobId, u.GoalId, u.ExecutionGeneration, u.InputTokens, u.OutputTokens }
                    }),
                foreignReservations = snapshot.ProviderBudgetReservations
                    .Where(r => r.State == "active" && ProviderBudgetLedger.HasForeignOwner(r))
                    .Select(r => new { r.Id, r.ProviderId, r.ProjectId, r.OwnerMachine, r.Reserved, r.Consumed }),
                usage = ProviderBudgetLedger.Summary(snapshot, settings),
                // The separate /api/usage endpoint provides observed Codex account windows.
                accountAllowances = "Unknown except where explicitly reported by /api/usage"
            });
        });
        app.MapPut("/api/projects/{id:guid}/providers", (Guid id, ProjectProvidersInput input) =>
            Results.Ok(ProjectProviderManager.Configure(store, settings, id, input)));
        app.MapPost("/api/providers/budget-reservations/{id:guid}/recover",
            (Guid id, ProviderHostRecoveryInput input) =>
                Results.Ok(ProviderBudgetReconciliation.HoldAfterHostRecovery(store, id, input.Reviewed, input.Reason)));
        app.MapPost("/api/providers/budget-reservations/{id:guid}/reconcile",
            (Guid id, ProviderBudgetReconciliationInput input) =>
                Results.Ok(ProviderBudgetReconciliation.Reconcile(store, id, input)));
        app.MapGet("/api/projects/{id:guid}/analysis", (Guid id) =>
            ProjectReviewer.Read(store, id) is { } review
                ? (IResult)Results.Ok(review) : Results.NotFound(new { error = "No review exists yet" }));
        app.MapPost("/api/projects/{id:guid}/analysis", async (Guid id, CancellationToken ct) =>
            Results.Ok(await ProjectReviewer.AnalyzeAsync(store, id, ct, settings)));
        app.MapPost("/api/projects/{id:guid}/analysis/goals", (Guid id, ReviewGoalInput input) =>
            Results.Created("/api/state", ProjectReviewer.CreateDraftGoal(store, id, input)));
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
            settings.LeaseSeconds, settings.PollSeconds, settings.AllowPush, settings.AutoMerge, settings.Runtime,
            settings.GoalReservationTokens, settings.GoalExtensionIncrementTokens,
            settings.GoalExtensionThresholdTokens, settings.MaxJobReservationTokens
        }));
        app.MapPut("/api/settings", (SettingsInput input) =>
        {
            if (input.MaxParallel is < 1 or > 32 || input.MaxAgentSessions is < 1 or > 64 ||
                input.MaxReviewRounds is < 1 or > 10 || input.LeaseSeconds is < 30 or > 3600 ||
                input.PollSeconds is < 1 or > 300 ||
                input.GoalReservationTokens is < 256 or > 65536 ||
                input.GoalExtensionIncrementTokens is < 256 or > 65536 ||
                input.GoalExtensionThresholdTokens is < 0 or > 65536 ||
                input.MaxJobReservationTokens is < 256 or > 1000000)
                return (IResult)Results.BadRequest(new { error = "Settings out of range" });
            var initial = input.GoalReservationTokens ?? settings.GoalReservationTokens;
            var max = input.MaxJobReservationTokens ?? settings.MaxJobReservationTokens;
            if (max < initial)
                return (IResult)Results.BadRequest(new { error = "Per-job reservation limit must be >= initial allocation" });
            if (input.AutoMerge)
                return (IResult)Results.BadRequest(new { error = "Automatic merging is not supported; merge through a manually approved PR" });
            settings.MaxParallel = input.MaxParallel;
            settings.MaxAgentSessions = input.MaxAgentSessions;
            settings.MaxReviewRounds = input.MaxReviewRounds;
            settings.LeaseSeconds = input.LeaseSeconds;
            settings.PollSeconds = input.PollSeconds;
            if (input.GoalReservationTokens.HasValue)
                settings.GoalReservationTokens = input.GoalReservationTokens.Value;
            if (input.GoalExtensionIncrementTokens.HasValue)
                settings.GoalExtensionIncrementTokens = input.GoalExtensionIncrementTokens.Value;
            if (input.GoalExtensionThresholdTokens.HasValue)
                settings.GoalExtensionThresholdTokens = input.GoalExtensionThresholdTokens.Value;
            if (input.MaxJobReservationTokens.HasValue)
                settings.MaxJobReservationTokens = input.MaxJobReservationTokens.Value;
            settings.AllowPush = input.AllowPush;
            settings.AutoMerge = false;
            var destination = Path.Combine(store.Home, "settings.json");
            var temporary = destination + ".tmp";
            File.WriteAllText(temporary, Json.Stringify(settings));
            File.Move(temporary, destination, true);
            store.Event(null, "settings_updated", "Execution settings updated through Mission Control");
            return Results.Ok(new { settings.MaxParallel, settings.MaxAgentSessions, settings.MaxReviewRounds,
                settings.LeaseSeconds, settings.PollSeconds, settings.AllowPush, settings.AutoMerge,
                settings.GoalReservationTokens, settings.GoalExtensionIncrementTokens,
                settings.GoalExtensionThresholdTokens, settings.MaxJobReservationTokens });
        });
        app.MapGet("/api/plans", () => Results.Ok(store.Read().Goals.Select(g =>
            new { g.Id, g.ProjectId, g.State, g.Objective, g.AcceptanceCriteria,
                GoalRevision = Planning.GoalRevision(g), Plan = MissionControlManager.ReadPlan(store, g.Id) })));
        app.MapGet("/api/plans/{goalId:guid}", (Guid goalId) =>
            MissionControlManager.ReadPlan(store, goalId) is { } plan ? (IResult)Results.Ok(plan) : Results.NotFound());
        app.MapGet("/api/goals/{goalId:guid}/preflight", (Guid goalId) =>
        {
            var snapshot = store.Read();
            var goal = snapshot.Goals.Single(g => g.Id == goalId);
            var project = snapshot.Projects.Single(p => p.Id == goal.ProjectId);
            return Results.Ok(WorkPackagePreflight.Inspect(snapshot, project, goal));
        });
        app.MapPut("/api/plans/{goalId:guid}", (Guid goalId, FactoryPlan plan) =>
        {
            if (plan.GoalId != goalId) return (IResult)Results.BadRequest(new { error = "Plan goal ID mismatch" });
            return Results.Ok(MissionControlManager.SavePlan(store, plan));
        });
        app.MapPost("/api/plans/{goalId:guid}/generate", async (Guid goalId, bool? wait, Guid? planRevision) =>
        {
            var state = store.Read();
            var goal = state.Goals.Single(g => g.Id == goalId);
            if (!SpecificationContracts.CanRevise(goal, state.Jobs))
                return (IResult)Results.Conflict(new { error = "Existing jobs prevent specification replacement." });
            var project = state.Projects.Single(p => p.Id == goal.ProjectId);
            if (wait == true)
                return Results.Ok(await Planning.ProposeAsync(store, project, goal, settings, CancellationToken.None, planRevision));
            _ = Task.Run(async () =>
            {
                store.Event(null, "plan_generating", goalId.ToString());
                try
                {
                    await Planning.ProposeAsync(store, project, goal, settings, CancellationToken.None, planRevision);
                    store.Event(null, "plan_generated", goalId.ToString());
                }
                catch (Exception ex) { store.Event(null, "plan_error", goalId + ": " + ex.Message); }
            });
            return Results.Accepted();
        });
        app.MapPost("/api/plans/{goalId:guid}/adopt", (Guid goalId, PlanAdoptInput input) =>
            Results.Ok(Planning.Adopt(store, goalId, input.Mode, input.ReviewedExistingWork, input.PlanRevision)));

        app.MapGet("/api/projects/{projectId:guid}/plan-policy", (Guid projectId) =>
            {
                var policy = store.ReadPlanPolicy(projectId);
                return Results.Ok(new { policy.Revision, rule = policy.Rule.ToString() });
            });
        app.MapPut("/api/projects/{projectId:guid}/plan-policy", (Guid projectId, PlanPolicyInput input) =>
            Results.Ok(new { revision = store.SetPlanPolicy(projectId, input) }));

        app.MapGet("/api/projects/{projectId:guid}/issue-intake-policy", (Guid projectId) =>
            Results.Ok(store.IssueIntakePolicy(projectId)));
        app.MapPut("/api/projects/{projectId:guid}/issue-intake-policy", (Guid projectId, IssueIntakePolicyInput input) =>
            Results.Ok(new { revision = store.SetIssueIntakePolicy(projectId, input) }));
        app.MapPost("/api/projects", (ProjectInput input) => Results.Created("/api/state", MissionControlManager.CreateProject(store, input)));
        app.MapPut("/api/projects/{id:guid}", (Guid id, ProjectInput input) => Results.Ok(MissionControlManager.UpdateProject(store, id, input)));
        app.MapPost("/api/projects/{id:guid}/action", (Guid id, ControlInput input) =>
            Results.Ok(MissionControlManager.SetProjectStatus(store, id, input.Action)));
        app.MapGet("/api/goals/{id:guid}/budget", (Guid id) =>
            Results.Ok(GoalBudgetLedger.Summary(store.Read(), id)));
        app.MapPost("/api/goals/{id:guid}/budget/reconcile", (Guid id, BudgetReconciliationInput input) =>
            Results.Ok(GoalBudgetLedger.Reconcile(store, id, input.ReservationId,
                input.FinalTokensIn, input.FinalTokensOut, input.Reviewed, input.Reason, input.ConfirmOverrun)));
        app.MapPost("/api/goals", (GoalInput input) => Results.Created("/api/state", MissionControlManager.CreateGoal(store, input)));
        app.MapPut("/api/goals/{id:guid}", (Guid id, GoalInput input) => Results.Ok(MissionControlManager.UpdateGoal(store, id, input)));
        app.MapPost("/api/goals/{id:guid}/action", (Guid id, ControlInput input) =>
            Results.Ok(MissionControlManager.SetGoalStatus(store, id, input.Action)));
        app.MapGet("/api/scheduler/decisions", () =>
        {
            var decisions = new List<SchedulingDecision>();
            JobScheduler.SelectWithReasons(store.Read(), settings.MaxParallel, JobExecutionModes.Local, null,
                settings.GoalReservationTokens, decisions);
            return Results.Ok(decisions);
        });
        app.MapPost("/api/jobs", (JobInput input) => Results.Created("/api/state", MissionControlManager.CreateJob(store, input, settings)));
        app.MapPut("/api/jobs/{id:guid}", (Guid id, JobInput input) => Results.Ok(MissionControlManager.UpdateJob(store, id, input)));
        // An explicit administrator-approved amendment is required for blocked scope violations.
        app.MapPost("/api/jobs/{id:guid}/scope-amendment", (Guid id, ScopeAmendmentInput input) =>
            Results.Ok(MissionControlManager.AmendScope(store, id, input)));
        app.MapPost("/api/jobs/{id:guid}/action", (Guid id, ControlInput input) =>
            string.IsNullOrEmpty(input.ControlRevision)
                ? Results.Conflict(new { error = "Refresh the job and include its control revision." })
                : Results.Ok(MissionControlManager.ControlJob(store, id, input)));
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
        app.MapGet("/api/jobs/{id:guid}/quality", (Guid id) =>
        {
            var summary = JobEvidenceReader.Read(store, id);
            return summary is null
                ? (IResult)Results.NotFound(new { error = "No durable quality evidence is available" })
                : Results.Ok(summary);
        });
        app.MapPost("/api/jobs/{id:guid}/reconcile", (Guid id, ControlInput input) =>
            Results.Ok(JobRecovery.ReconcileLocal(store, id, input.Reviewed, input.Reason)));
        app.MapPost("/api/jobs/{id:guid}/release-resources", (Guid id, ResourceReleaseInput input) =>
            Results.Ok(JobRecovery.ReleaseResources(store, id, input)));
        app.MapPost("/api/jobs/{id:guid}/verify", async (Guid id, CancellationToken ct) =>
        {
            var proof = await new FactoryEngine(store, settings).VerifyIntegrationAsync(id, ct);
            return Results.Ok(proof);
        });
        // Registration through Mission Control is administrator-only.
        app.MapPost("/api/workers/register", (WorkerInput input) => Results.Ok(MissionControlManager.RegisterWorker(store, input)));
        // Only the administrator credential can replace a worker's project grants
        // and independently reviewed capability evidence. Worker tokens cannot reach this route.
        app.MapPut("/api/workers/{id}/permissions", (string id, WorkerPermissionsInput input) =>
            Results.Ok(MissionControlManager.SetWorkerPermissions(store, id, input)));
        // Remote workers have a distinct API namespace with worker-scoped credentials.
        app.MapPost("/api/worker/register", (HttpContext ctx, WorkerInput input) =>
        {
            if (!WorkerAuthentication.MatchesIdentity(ctx.Request.Headers[WorkerAuthentication.WorkerIdHeader].ToString(), input.Id))
                return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(MissionControlManager.RegisterWorker(store, input));
        });
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
                try { MissionControlManager.ControlJob(store, j.Id, new ControlInput { Action = "stop", Reason = "Queue stop", ControlRevision = j.ControlRevision }); requested.Add(j.Id); }
                catch (InvalidOperationException) { }
            }
            return Results.Ok(new { paused = true, cancellationRequested = requested,
                note = "Remote jobs cannot be force-stopped. Disable/drain remote workers." });
        });
        app.MapPost("/api/queue/recover", () => Results.Ok(new FactoryEngine(store, settings).Recover()));
        app.MapPost("/api/worker/claim", (HttpContext ctx, WorkerClaimRequest req) =>
        {
            if (!WorkerAuthentication.MatchesIdentity(ctx.Request.Headers[WorkerAuthentication.WorkerIdHeader].ToString(), req.WorkerId))
                return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!req.SupportsStartAdmission)
                return Results.Conflict(new { error = "Upgrade the worker to use one-shot start admission." });
            var claim = WorkerProtocol.Claim(store, settings, req.WorkerId, req.ProtocolVersion);
            return claim is null ? (IResult)Results.NoContent() : Results.Ok(claim);
        });
        app.MapPost("/api/executor/turn", async (RemoteExecutorGrant grant, CancellationToken ct) =>
            Results.Ok(await RemoteProviderExecutor.RunAsync(store, settings, grant, ct)));
        app.MapPost("/api/executor/receipt", async (RemoteProviderExecutor.ReceiptQuery query, CancellationToken ct) =>
            await RemoteProviderExecutor.ReadReceiptAsync(store, query, ct) is { } receipt
                ? (IResult)Results.Ok(receipt) : Results.NotFound());
        app.MapPost("/api/jobs/{id:guid}/provider-turns/{turnId:guid}/recover",
            async (Guid id, Guid turnId, RemoteReceiptRecoveryInput input, CancellationToken ct) =>
                Results.Ok(await RemoteProviderRouting.RecoverReceiptAsync(store, settings, id, turnId, input, ct)));
        app.MapGet("/api/jobs/{id:guid}/provider-turns", (Guid id) =>
        {
            var snapshot = store.Read();
            var job = snapshot.Jobs.SingleOrDefault(job => job.Id == id);
            if (job is null) return (IResult)Results.NotFound();
            var turns = from lease in snapshot.RemoteProviderLeases
                        where lease.JobId == id
                        join turn in snapshot.RemoteProviderTurns on lease.Id equals turn.LeaseId
                        join usage in snapshot.ProviderUsages on turn.UsageId equals usage.Id
                        join reservation in snapshot.ProviderBudgetReservations on usage.Id equals reservation.UsageId
                        orderby turn.StartedAt descending
                        select new
                        {
                            turn.Id, lease.ExecutionGeneration, turn.Role, usage.ProviderId, requestedModel = usage.Model,
                            turn.ObservedModel, reservation.Reserved, reservation.Consumed, turn.State, turn.HoldReason,
                            usage.FinalUsageReported, usage.ReportedCostMicros,
                            canRecoverReceipt = RemoteProviderRouting.CanRecoverReceipt(turn, lease, job, settings)
                        };
            return Results.Ok(turns.Take(100).ToArray());
        });
        app.MapPost("/api/worker/turn", async (HttpContext ctx, RemoteProviderTurnRequest req, CancellationToken ct) =>
        {
            if (!WorkerAuthentication.MatchesIdentity(ctx.Request.Headers[WorkerAuthentication.WorkerIdHeader].ToString(), req.WorkerId))
                return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await RemoteProviderRouting.ExecuteAsync(store, settings, req, ct));
        });
        app.MapPost("/api/worker/start", (HttpContext ctx, WorkerStartRequest req) =>
        {
            if (!WorkerAuthentication.MatchesIdentity(ctx.Request.Headers[WorkerAuthentication.WorkerIdHeader].ToString(), req.WorkerId))
                return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
            return store.StartRemoteBuilder(req, settings) ? (IResult)Results.Ok() : Results.Conflict();
        });
        app.MapPost("/api/worker/heartbeat", (HttpContext ctx, WorkerStatus req) =>
        {
            if (!WorkerAuthentication.MatchesIdentity(ctx.Request.Headers[WorkerAuthentication.WorkerIdHeader].ToString(), req.WorkerId))
                return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
            return WorkerProtocol.Heartbeat(store, req, settings) ? (IResult)Results.Ok() : Results.Conflict();
        });
        app.MapPost("/api/worker/budget/extend", (HttpContext ctx, WorkerBudgetExtensionRequest req) =>
        {
            if (!WorkerAuthentication.MatchesIdentity(
                    ctx.Request.Headers[WorkerAuthentication.WorkerIdHeader].ToString(), req.WorkerId))
                return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
            var grant = WorkerProtocol.ExtendBudget(store, settings, req);
            return grant is null ? (IResult)Results.Conflict(new
                { error = "No active authorized remote execution or extension capacity" }) : Results.Ok(grant);
        });
        app.MapPost("/api/worker/complete", async (HttpContext ctx, WorkerStatus req, CancellationToken ct) =>
        {
            if (!WorkerAuthentication.MatchesIdentity(ctx.Request.Headers[WorkerAuthentication.WorkerIdHeader].ToString(), req.WorkerId))
                return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
            if (req.Evidence is { Length: > 1_000_000 })
                return (IResult)Results.BadRequest(new { error = "Evidence too large" });
            // Do not persist worker-supplied evidence or PR metadata before independent verification.
            var accepted = await WorkerProtocol.CompleteAsync(store, req,
                new GitHubWorkerResultVerifier(), ct, settings: settings);
            return accepted ? (IResult)Results.Ok() : Results.Conflict();
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
    public Guid PlanRevision { get; set; }
    public string Mode { get; set; } = "local";
    public bool ReviewedExistingWork { get; set; }
}

public sealed class BudgetReconciliationInput
{
    public bool ConfirmOverrun { get; set; }
    public Guid ReservationId { get; set; }
    public long FinalTokensIn { get; set; }
    public long FinalTokensOut { get; set; }
    public bool Reviewed { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class ProviderHostRecoveryInput
{
    public bool Reviewed { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class SettingsInput
{
    public int MaxParallel { get; set; } = 3;
    // Optional to preserve existing custom settings when older UI clients update settings.
    public int? GoalReservationTokens { get; set; }
    public int? GoalExtensionIncrementTokens { get; set; }
    public int? GoalExtensionThresholdTokens { get; set; }
    public int? MaxJobReservationTokens { get; set; }
    public int MaxAgentSessions { get; set; } = 6;
    public int MaxReviewRounds { get; set; } = 3;
    public int LeaseSeconds { get; set; } = 120;
    public int PollSeconds { get; set; } = 3;
    public bool AllowPush { get; set; }
    public bool AutoMerge { get; set; }
}
