using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Factory;

public sealed class WorkerClaimRequest { public string WorkerId { get; set; } = ""; public int ProtocolVersion { get; set; } public bool SupportsStartAdmission { get; set; } }
public sealed record WorkerStartRequest(string WorkerId, Guid ProjectId, Guid JobId, long ExecutionGeneration,
    string LeaseToken, Guid? ProviderLeaseId);
public sealed class WorkerClaim
{
    public string WorkerId { get; set; } = "";
    public FactoryJob Job { get; set; } = new();
    public ProjectInfo Project { get; set; } = new();
    public FactoryGoal? Goal { get; set; }
    public FactorySettings Settings { get; set; } = new();
    public Guid? ProviderLeaseId { get; set; }
    public RemoteProviderRoute[]? ProviderRoutes { get; set; }
}
public sealed class WorkerStatus
{
    public string WorkerId { get; set; } = "";
    public Guid JobId { get; set; }
    public FactoryJob? Job { get; set; }
    public string Evidence { get; set; } = "";
    public string Error { get; set; } = "";
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public long ExecutionGeneration { get; set; }
    public string LeaseToken { get; set; } = "";
    public bool UsageComplete { get; set; }
    public Guid? ProviderLeaseId { get; set; }
}

public sealed class WorkerBudgetExtensionRequest
{
    public string WorkerId { get; set; } = "";
    public Guid JobId { get; set; }
    public long ExecutionGeneration { get; set; }
    public string LeaseToken { get; set; } = "";
    public Guid RequestId { get; set; }
    public int RequestedTokens { get; set; }
}

public sealed class WorkerBudgetExtensionGrant
{
    public Guid RequestId { get; set; }
    public long Granted { get; set; }
    public long Reserved { get; set; }
    public long ExecutionGeneration { get; set; }
}

public static class WorkerProtocol
{
    public static bool Authorized(string? bearer, string token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(bearer) ||
            !bearer.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        var provided = bearer[7..];
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    public static WorkerClaim? Claim(StateStore store, FactorySettings settings, string workerId, int protocolVersion = 0)
    {
        if (!WorkerAuthentication.IsValidWorkerId(workerId)) return null;
        JobRecovery.QuarantineExpired(store, JobExecutionModes.Remote);
        return store.Change((s, db, tx) =>
        {
            if (s.Paused || !settings.AllowPush) return null;
           var registered = s.Workers.FirstOrDefault(w => w.Id == workerId);
           // Worker credentials can only be used with a registered, enabled identity.
            if (registered is null || !registered.Enabled || registered.Draining) return null;
            // Apply the same mode, dependency, budget, capacity and conflict gates as local runners.
            var selected = JobScheduler.Select(s, settings.MaxParallel, JobExecutionModes.Remote,
                job =>
                {
                    var project = s.Projects.FirstOrDefault(p => p.Id == job.ProjectId);
                    // Explicit provider policy requires the versioned lease path.
                    if (project is null || string.IsNullOrWhiteSpace(project.Origin) ||
                        !WorkerAuthorization.CanExecute(s, registered, project, job, settings)) return false;
                    return RemoteProviderRouting.Required(project, job)
                        ? protocolVersion == RemoteProviderRouting.ProtocolVersion && RemoteProviderRouting.Plan(s, settings, job) is not null
                        : !s.ProviderAssignments.Any(a => a.ProjectId == project.Id) &&
                          !s.ProviderBudgetLimits.Any(b => b.ProjectId == project.Id) &&
                          !settings.Providers.Any(p => p.TokenLimit > 0);
                },
                reservationTokens: settings.GoalReservationTokens);
            foreach (var job in selected)
            {
                var project = s.Projects.Single(p => p.Id == job.ProjectId);
                if (job.GoalId is not null &&
                    GoalBudgetLedger.Allocation(s, job, settings.GoalReservationTokens) == 0)
                    continue;
                var requiresProviderLease = RemoteProviderRouting.Required(project, job);
                var routes = requiresProviderLease ? RemoteProviderRouting.Plan(s, settings, job) : null;
                if (requiresProviderLease && routes is null) continue;
                JobRecovery.Assign(job, "remote:" + workerId, settings.LeaseSeconds, local: false,
                    state: s, reservationTokens: settings.GoalReservationTokens);
                StateStore.RecordExecutionProvenance(db, tx, project, job,
                    routes?.Single(route => route.Role == "builder").Runtime ?? settings.Runtime,
                    s.WorkerCapabilityProofs.Where(proof => proof.WorkerId == workerId &&
                        proof.VerifiedAt <= DateTimeOffset.UtcNow && proof.VerifiedAt > DateTimeOffset.UtcNow.AddDays(-30) &&
                        WorkerAuthorization.IsKnownCapability(proof.Capability) && !string.IsNullOrWhiteSpace(proof.Evidence)));
                StateStore.RecordLifecycle(db, tx, job.ProjectId, "job", job.Id, job.LeaseOwner, JobStates.Queued,
                    job.State, LifecycleReason.LeaseClaimed, generation: job.ExecutionGeneration, state: s);
                RemoteProviderLease? providerLease = null;
                if (routes is not null)
                {
                    providerLease = new RemoteProviderLease
                    {
                        JobId = job.Id, ExecutionGeneration = job.ExecutionGeneration, WorkerId = workerId,
                        ExecutorUrl = settings.RemoteProviderExecutorUrl, TurnTokens = settings.RemoteProviderTurnTokens,
                        MaxJobTokens = settings.MaxJobReservationTokens,
                        RouteJson = Json.Stringify(routes)
                    };
                    s.RemoteProviderLeases.Add(providerLease);
                }
                FactoryGoal? goalCopy = null;
                if (job.GoalId is { } goalId)
                {
                    var goal = s.Goals.Single(g => g.Id == goalId);
                    var reservation = GoalBudgetLedger.Current(s, job);
                    // A delegated worker can only spend the capacity already reserved
                    // atomically by the controller. Its own state is not authoritative.
                    goalCopy = new FactoryGoal
                    {
                        Id = goal.Id, ProjectId = goal.ProjectId, State = goal.State,
                        Objective = goal.Objective, AcceptanceCriteria = goal.AcceptanceCriteria.ToList(),
                        TokenBudget = checked(job.TokensIn + job.TokensOut + reservation.Reserved),
                        CreatedAt = goal.CreatedAt
                    };
                }
               registered.Heartbeat = DateTimeOffset.UtcNow;
               s.Events.Add(new FactoryEvent { JobId = job.Id, Type = "remote_claim", Message = workerId });
               return new WorkerClaim
                {
                    WorkerId = workerId,
                    ProviderLeaseId = providerLease?.Id, ProviderRoutes = routes,
                    Project = project, Job = job, Goal = goalCopy,
                    Settings = new FactorySettings
                    {
                    Runtime = routes?.Single(route => route.Role == "builder").Runtime ?? settings.Runtime, Model = settings.Model,
                        MaxReviewRounds = settings.MaxReviewRounds, AllowPush = true,
                        LeaseSeconds = settings.LeaseSeconds, MaxParallel = 1,
                        GoalReservationTokens = job.GoalId is null
                            ? settings.GoalReservationTokens
                            : checked((int)Math.Min(int.MaxValue, GoalBudgetLedger.Current(s, job).Reserved)),
                        GoalExtensionIncrementTokens = settings.GoalExtensionIncrementTokens,
                        GoalExtensionThresholdTokens = settings.GoalExtensionThresholdTokens,
                        MaxJobReservationTokens = settings.MaxJobReservationTokens
                    }
                };
           }
            return null;
        });
    }

    public static WorkerBudgetExtensionGrant? ExtendBudget(StateStore store,
        FactorySettings settings, WorkerBudgetExtensionRequest request)
    {
        if (!WorkerAuthentication.IsValidWorkerId(request.WorkerId) ||
            request.RequestId == Guid.Empty || request.JobId == Guid.Empty ||
            request.RequestedTokens is < 256 or > 1000000)
            return null;
        return store.Change(state =>
        {
            var worker = state.Workers.FirstOrDefault(w => w.Id == request.WorkerId);
            var job = state.Jobs.FirstOrDefault(j => j.Id == request.JobId);
            var project = job is null ? null : state.Projects.FirstOrDefault(p => p.Id == job.ProjectId);
            var fence = new ExecutionFence(request.ExecutionGeneration,
                request.LeaseToken, "remote:" + request.WorkerId);
            if (worker is null || job is null || project is null ||
                !WorkerAuthorization.CanContinue(state, worker, project, job, settings) ||
                !JobRecovery.Matches(job, fence) ||
                job.Mode != JobExecutionModes.Remote || job.GoalId is null)
                return null;
            var providerLease = RemoteProviderRouting.Current(state, job);
            if (providerLease is not null && !RemoteProviderRouting.CanContinue(state, settings, job, providerLease))
                return null;
            var extension = GoalBudgetLedger.ExtendRemote(state, job, fence,
                request.RequestId, request.RequestedTokens, settings.GoalExtensionIncrementTokens,
                settings.MaxJobReservationTokens);
            return new WorkerBudgetExtensionGrant
            {
                RequestId = request.RequestId, Granted = extension.Granted,
                Reserved = GoalBudgetLedger.Current(state, job).Reserved,
                ExecutionGeneration = job.ExecutionGeneration
            };
        });
    }

    public static bool Heartbeat(StateStore store, WorkerStatus req, FactorySettings settings)
    {
        // Idle heartbeat updates are independent from job authorization and usage.
        if (req.JobId == Guid.Empty) return store.TouchWorkerHeartbeat(req.WorkerId);
        return store.Change((s, db, tx) =>
        {
            var worker = s.Workers.FirstOrDefault(w => w.Id == req.WorkerId);
            if (worker is null || !worker.Enabled) return false;
            var j = s.Jobs.FirstOrDefault(j => j.Id == req.JobId);
            var project = j is null ? null : s.Projects.FirstOrDefault(p => p.Id == j.ProjectId);
            if (j is null || project is null ||
                !WorkerAuthorization.CanContinue(s, worker, project, j, settings) ||
                !JobRecovery.Matches(j,
                new ExecutionFence(req.ExecutionGeneration, req.LeaseToken, "remote:" + req.WorkerId)))
                return false;
            var providerLease = RemoteProviderRouting.Current(s, j);
            if (providerLease is not null)
            {
                if (req.ProviderLeaseId != providerLease.Id ||
                    !RemoteProviderRouting.CanContinue(s, settings, j, providerLease))
                {
                    StateStore.RecordLifecycle(db, tx, j.ProjectId, "job", j.Id, j.LeaseOwner, j.State,
                        JobStates.Blocked, LifecycleReason.LeaseFenced, generation: j.ExecutionGeneration, state: s);
                    RemoteProviderRouting.Fence(s, j, "Remote provider lease no longer authorized");
                    return false;
                }
                worker.Heartbeat = DateTimeOffset.UtcNow;
                j.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(settings.LeaseSeconds);
                return true;
            }
            if (req.TokensIn < 0 || req.TokensOut < 0) return false;
           worker.Heartbeat = DateTimeOffset.UtcNow;
           var remaining = GoalBudgetLedger.Record(s, j, req.TokensIn, req.TokensOut,
               req.UsageComplete);
           if (remaining < 0)
           {
               GoalBudgetLedger.Hold(s, j, "remote worker exceeded its reserved budget");
               StateStore.RecordLifecycle(db, tx, j.ProjectId, "job", j.Id, j.LeaseOwner, j.State,
                   JobStates.Blocked, LifecycleReason.LeaseFenced, generation: j.ExecutionGeneration, state: s);
               j.ExecutionGeneration++;
               j.State = JobStates.Blocked; j.Stage = "reconcile";
               j.Error = "Remote worker budget exceeded; review observed usage";
               j.LeaseOwner = ""; j.LeaseToken = ""; j.LeaseUntil = null;
               s.Events.Add(new FactoryEvent { JobId = j.Id, Type = "budget_overrun",
                   Message = "Reported usage exceeds delegated reservation" });
               return false;
           }
           j.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(settings.LeaseSeconds);
           return true;
        });
    }

    public static async Task<bool> CompleteAsync(StateStore store, WorkerStatus req,
        IWorkerResultVerifier verifier, CancellationToken ct = default,
        FactorySettings? settings = null)
    {
        // Worker-supplied fields cannot authorize a new claim or extend a foreign lease.
        var state = store.Read();
        var claimed = state.Jobs.FirstOrDefault(j => j.Id == req.JobId);
        var worker = state.Workers.FirstOrDefault(w => w.Id == req.WorkerId);
        var executionSettings = settings ?? new FactorySettings();
        var claimProject = claimed is null ? null :
            state.Projects.FirstOrDefault(p => p.Id == claimed.ProjectId);
        if (claimed is null || worker is null || claimProject is null ||
            !WorkerAuthorization.CanContinue(state, worker, claimProject, claimed, executionSettings) ||
            claimed.Mode != JobExecutionModes.Remote ||
            !JobRecovery.Matches(claimed,
                new ExecutionFence(req.ExecutionGeneration, req.LeaseToken, "remote:" + req.WorkerId)))
            return false;
        var project = state.Projects.Single(p => p.Id == claimed.ProjectId);
        var providerLease = RemoteProviderRouting.Current(state, claimed);
        if (req.ProviderLeaseId != providerLease?.Id) return false;
        var successReported = req.Job is { State: JobStates.PrReady };
        VerifiedWorkerResult? verified = null;
        var failure = "Worker returned no verified PR result";
        if (successReported)
        {
            try
            {
                if (req.Job!.Id != claimed.Id || req.Job.ProjectId != claimed.ProjectId ||
                    req.Job.Attempt != claimed.Attempt ||
                    req.Job.Mode != JobExecutionModes.Remote)
                    throw new InvalidOperationException("Worker completion does not match its active job");
                verified = await verifier.VerifyAsync(project, claimed, req, ct);
                if (verified is null || string.IsNullOrWhiteSpace(verified.EvidenceJson))
                    throw new InvalidOperationException("Controller verification returned no evidence");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failure = "Unverified remote result: " + ex.Message; }
        }
        else
            failure = string.IsNullOrWhiteSpace(req.Error) ? "Remote worker did not report a verified PR" : req.Error;

        string? evidenceFile = null;
        if (verified is not null)
        {
            try
            {
                var dir = Path.Combine(store.Home, "evidence", claimed.Id.ToString("N"));
                Directory.CreateDirectory(dir);
                evidenceFile = Path.Combine(dir, "controller-" + req.Job!.HeadSha + ".json");
                await File.WriteAllTextAsync(evidenceFile, verified.EvidenceJson, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                verified = null;
                failure = "Unable to save controller-verified evidence: " + ex.Message;
            }
        }

        return store.Change((s, db, tx) =>
        {
            var j = s.Jobs.FirstOrDefault(x => x.Id == req.JobId);
            var currentProject = s.Projects.FirstOrDefault(p => p.Id == claimed.ProjectId);
            var currentWorker = s.Workers.FirstOrDefault(w => w.Id == req.WorkerId);
            if (j is null || currentWorker is null || currentProject is null ||
                !WorkerAuthorization.CanContinue(s, currentWorker, currentProject, j, executionSettings) ||
                j.Mode != JobExecutionModes.Remote ||
                !JobRecovery.Matches(j,
                    new ExecutionFence(req.ExecutionGeneration, req.LeaseToken, "remote:" + req.WorkerId)) ||
                j.Attempt != claimed.Attempt || j.ProjectId != claimed.ProjectId ||
                currentProject is null ||
                // A project reconfiguration invalidates verification performed against an older policy.
                Json.Stringify(currentProject.Checks) != Json.Stringify(project.Checks) ||
                Json.Stringify(currentProject.ProtectedPaths) != Json.Stringify(project.ProtectedPaths) ||
                currentProject.Origin != project.Origin || currentProject.BaseBranch != project.BaseBranch)
                return false;

            // Reported cumulative usage is debited under the same transaction as
            // completion. Fenced stale completions never modify the budget ledger.
            long remaining;
            var finalUsage = req.UsageComplete;
            if (providerLease is not null)
            {
                var current = RemoteProviderRouting.Current(s, j);
                finalUsage = current?.Id == providerLease.Id &&
                    RemoteProviderRouting.CanContinue(s, executionSettings, j, providerLease) &&
                    verified?.TreeSha is { } tree &&
                    RemoteProviderRouting.VerifyCompletion(s, providerLease, req.Job!.HeadSha, tree);
                if (!finalUsage)
                {
                    failure = "Missing or inconsistent trusted provider turn evidence";
                    RemoteProviderRouting.HoldTurns(s, j, failure);
                }
                remaining = GoalBudgetLedger.Record(s, j, j.TokensIn, j.TokensOut, finalUsage);
            }
            else
            {
                var reportedIn = Math.Max(req.TokensIn, req.Job?.TokensIn ?? 0);
                var reportedOut = Math.Max(req.TokensOut, req.Job?.TokensOut ?? 0);
                if (reportedIn < 0 || reportedOut < 0) return false;
                remaining = GoalBudgetLedger.Record(s, j, reportedIn, reportedOut, finalUsage);
            }

            var previousState = j.State;
            if (verified is not null && successReported &&
                (j.GoalId is null || (finalUsage && remaining >= 0)))
            {
                j.State = JobStates.PrReady; j.Stage = "pr";
                j.Branch = req.Job!.Branch; j.PrUrl = req.Job.PrUrl;
                j.HeadSha = req.Job.HeadSha; j.EvidenceFile = evidenceFile!;
                j.Error = "";
                s.Events.Add(new FactoryEvent { JobId = j.Id, Type = "remote_verified",
                    Message = "Controller verified PR head and required GitHub checks" });
            }
            else
            {
                if (j.GoalId is not null && (remaining < 0 || !finalUsage) &&
                    successReported && verified is not null)
                    failure = "Unverified or exhausted final goal usage";
                var scopeBlocked = req.Job is { State: JobStates.Blocked, Stage: "scope" } candidate &&
                    candidate.Id == j.Id && candidate.ProjectId == j.ProjectId &&
                    candidate.Attempt == j.Attempt && candidate.Mode == JobExecutionModes.Remote;
                j.State = JobStates.Blocked;
                j.Stage = scopeBlocked ? "scope" : "remote_verification_failed";
                j.Error = scopeBlocked ? req.Job!.Error : failure;
                s.Events.Add(new FactoryEvent { JobId = j.Id,
                    Type = scopeBlocked ? "scope_violation" : "remote_rejected", Message = j.Error });
            }
            StateStore.RecordLifecycle(db, tx, j.ProjectId, "job", j.Id, j.LeaseOwner, previousState, j.State,
                j.State == JobStates.PrReady ? LifecycleReason.RemoteResultVerified : LifecycleReason.RemoteResultRejected,
                reference: j.State == JobStates.PrReady ? j.HeadSha : null, generation: j.ExecutionGeneration, state: s);
            GoalBudgetLedger.Settle(s, j);
            j.LeaseOwner = ""; j.LeaseToken = ""; j.LeaseUntil = null;
            j.UpdatedAt = DateTimeOffset.UtcNow;
            j.FinishedAt = DateTimeOffset.UtcNow;
            s.Events.Add(new FactoryEvent { JobId = j.Id, Type = "remote_complete", Message = j.State });
            return true;
        });
    }

}

public static class RemoteWorker
{
    public static async Task RunAsync(Uri server, string token, string workspace, string workerId,
        CancellationToken ct)
    {
        if (!server.IsLoopback && server.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Remote workers require HTTPS (or loopback) for API tokens");
        if (!WorkerAuthentication.IsValidWorkerId(workerId) || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Worker ID and scoped credential are required");
        using var http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Add(WorkerAuthentication.WorkerIdHeader, workerId);
        Directory.CreateDirectory(workspace);
        using (var register = await http.PostAsJsonAsync("api/worker/register",
            new WorkerInput { Id = workerId, Platform = Environment.OSVersion.Platform.ToString() }, ct))
            register.EnsureSuccessStatusCode();
        while (!ct.IsCancellationRequested)
        {
            using var claimResponse = await http.PostAsJsonAsync("api/worker/claim", new WorkerClaimRequest
            { WorkerId = workerId, ProtocolVersion = RemoteProviderRouting.ProtocolVersion, SupportsStartAdmission = true }, ct);
           claimResponse.EnsureSuccessStatusCode();
           if (claimResponse.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                try { await http.PostAsJsonAsync("api/worker/heartbeat", new WorkerStatus { WorkerId = workerId }, ct); }
                catch (HttpRequestException) { }
                await Task.Delay(3000, ct); continue;
           }
            var claim = await claimResponse.Content.ReadFromJsonAsync<WorkerClaim>(Json.Options, ct)
                ?? throw new IOException("Empty worker claim");
            if (claim.WorkerId != workerId || claim.Job.ProjectId != claim.Project.Id ||
                !BuilderExecutionProfile.Allows(claim.Project, claim.Job) ||
                !BuilderExecutionProfile.MatchesRuntime(claim.Project, claim.Job, claim.Settings.Runtime) ||
                (claim.ProviderLeaseId is not null &&
                    claim.ProviderRoutes?.SingleOrDefault(route => route.Role == "builder")?.Runtime != claim.Settings.Runtime) ||
                !JobExecutionModes.Matches(claim.Job, JobExecutionModes.Remote))
                throw new InvalidOperationException("Remote worker received a job for another execution mode");
           var jobDir = Path.Combine(workspace, claim.Job.Id.ToString("N"));
           Directory.CreateDirectory(jobDir);
           var localState = new StateStore(Path.Combine(jobDir, "state"));
           var report = new WorkerStatus { WorkerId = workerId, JobId = claim.Job.Id,
                TokensIn = claim.Job.TokensIn, TokensOut = claim.Job.TokensOut,
                ExecutionGeneration = claim.Job.ExecutionGeneration,
                LeaseToken = claim.Job.LeaseToken, ProviderLeaseId = claim.ProviderLeaseId };
           if (!localState.ConsumeWorkerClaim(report.JobId, report.ExecutionGeneration)) continue;
           using (var admitted = await http.PostAsJsonAsync("api/worker/start",
               new WorkerStartRequest(workerId, claim.Project.Id, report.JobId, report.ExecutionGeneration,
                   report.LeaseToken, report.ProviderLeaseId), ct))
               if (!admitted.IsSuccessStatusCode) continue;
           using var executionCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
           using var heartbeatCancel = CancellationTokenSource.CreateLinkedTokenSource(executionCancel.Token);
           var heartbeat = Task.Run(async () =>
            {
                while (!heartbeatCancel.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, claim.Settings.LeaseSeconds / 3)), heartbeatCancel.Token); }
                    catch (OperationCanceledException) { break; }
                    try
                    {
                        var current = localState.Read().Jobs.FirstOrDefault(j => j.Id == claim.Job.Id);
                        if (current != null)
                        {
                            report.TokensIn = current.TokensIn;
                            report.TokensOut = current.TokensOut;
                            report.UsageComplete = current.UsageComplete;
                        }
                        using var response = await http.PostAsJsonAsync("api/worker/heartbeat",
                            report, heartbeatCancel.Token);
                        response.EnsureSuccessStatusCode();
                    }
                    catch (OperationCanceledException) when (heartbeatCancel.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        // A rejected, missing or stale heartbeat revokes permission
                        // to continue this worker's external execution.
                        executionCancel.Cancel();
                        break;
                    }
                }
            }, heartbeatCancel.Token);
           try
            {
                Directory.CreateDirectory(jobDir);
               var repoPath = Path.Combine(jobDir, "repository");
               if (!Directory.Exists(Path.Combine(repoPath, ".git")))
                {
                    var clone = await Commands.RunAsync("git", ["clone", "--branch", claim.Project.BaseBranch,
                        "--single-branch", claim.Project.Origin, repoPath], jobDir, 300, cancellationToken: executionCancel.Token);
                   Commands.Require(clone, "git clone");
               }
                claim.Project.Path = repoPath;
               claim.Project.Origin = GitOperations.Origin(repoPath) ?? claim.Project.Origin;
               // The controller already counted the remote claim. The local execution claim
               // must not increment the same attempt a second time.
               claim.Job.Attempt = Math.Max(0, claim.Job.Attempt - 1);
               claim.Job.State = JobStates.Queued;
               claim.Job.DependsOn = [];
               claim.Job.LeaseOwner = ""; claim.Job.LeaseUntil = null;
               localState.Change(s =>
                {
                    if (s.Projects.All(p => p.Id != claim.Project.Id)) s.Projects.Add(claim.Project);
                   if (claim.Goal != null)
                    {
                        var previousGoal = s.Goals.FirstOrDefault(g => g.Id == claim.Goal.Id);
                        if (previousGoal is null) s.Goals.Add(claim.Goal);
                        else
                        {
                            previousGoal.TokenBudget = claim.Goal.TokenBudget;
                            previousGoal.State = claim.Goal.State;
                            previousGoal.Objective = claim.Goal.Objective;
                            previousGoal.AcceptanceCriteria = claim.Goal.AcceptanceCriteria;
                        }
                    }
                    // Recovered local workspaces must refresh their claim and usage counters.
                    s.Jobs.RemoveAll(j => j.Id == claim.Job.Id);
                   s.Jobs.Add(claim.Job);
               });
               claim.Settings.WorkspaceRoot = Path.Combine(jobDir, "worktrees");
               Guid? pendingGrantRequestId = null;
               async Task RequestControllerExtension(Guid targetJobId, CancellationToken grantCancellation)
               {
                   if (targetJobId != report.JobId || claim.Goal is null)
                       throw new InvalidOperationException("Remote grant requested for another job");
                   pendingGrantRequestId ??= Guid.NewGuid();
                   var request = new WorkerBudgetExtensionRequest
                   {
                       WorkerId = workerId, JobId = report.JobId,
                       ExecutionGeneration = report.ExecutionGeneration,
                       LeaseToken = report.LeaseToken,
                       RequestId = pendingGrantRequestId.Value,
                       RequestedTokens = claim.Settings.GoalExtensionIncrementTokens
                   };
                   using var response = await http.PostAsJsonAsync(
                       "api/worker/budget/extend", request, grantCancellation);
                   if (!response.IsSuccessStatusCode)
                       throw new TokenBudgetExceededException(
                           "Controller did not authorize additional remote goal capacity");
                   var grant = await response.Content.ReadFromJsonAsync<WorkerBudgetExtensionGrant>(
                       Json.Options, grantCancellation)
                       ?? throw new IOException("Missing controller grant");
                   if (grant.RequestId != request.RequestId ||
                       grant.ExecutionGeneration != report.ExecutionGeneration)
                       throw new InvalidOperationException("Controller grant identity mismatch");
                   localState.Change(s =>
                   {
                       var current = s.Jobs.Single(j => j.Id == targetJobId);
                       JobRecovery.Require(current, new ExecutionFence(
                           current.ExecutionGeneration, current.LeaseToken, current.LeaseOwner));
                       var reservation = GoalBudgetLedger.Current(s, current);
                       if (grant.Reserved < reservation.Reserved)
                           throw new InvalidOperationException("Controller grant cannot reduce local reservation");
                       if (grant.Reserved == reservation.Reserved) return;
                       var added = grant.Reserved - reservation.Reserved;
                       reservation.Reserved = grant.Reserved;
                       reservation.UpdatedAt = DateTimeOffset.UtcNow;
                       s.Goals.Single(g => g.Id == reservation.GoalId).TokenBudget =
                           checked(reservation.BaseUsage + grant.Reserved);
                       s.BudgetExtensions.Add(new GoalBudgetExtension
                       {
                           Id = grant.RequestId, GoalId = reservation.GoalId,
                           JobId = current.Id, ReservationId = reservation.Id,
                           ExecutionGeneration = current.ExecutionGeneration,
                           WorkerId = "remote:" + workerId, Granted = added,
                           AuthorizedTotal = reservation.Reserved
                       });
                       s.Events.Add(new FactoryEvent { JobId = current.Id, Type = "budget_authorized",
                           Message = "Controller-authorized remote capacity synchronized" });
                   });
                   pendingGrantRequestId = null;
               }
               var engine = new FactoryEngine(localState, claim.Settings,
                   executionMode: JobExecutionModes.Remote,
                   remoteBudgetExtension: RequestControllerExtension,
                   remoteProviderRuntime: claim.ProviderLeaseId is null ? null : role =>
                       new RemoteProviderAgent(server, token, claim, role));
               await engine.RunQueueAsync(executionCancel.Token);
               report.Job = localState.Job(claim.Job.Id);
               report.TokensIn = report.Job.TokensIn;
               report.TokensOut = report.Job.TokensOut;
               report.UsageComplete = report.Job.UsageComplete;
               if (!string.IsNullOrWhiteSpace(report.Job.EvidenceFile) && File.Exists(report.Job.EvidenceFile))
                    report.Evidence = await File.ReadAllTextAsync(report.Job.EvidenceFile, ct);
               report.Error = report.Job.Error;
           }
            catch (Exception e) when (e is not OperationCanceledException) { report.Error = e.Message; }
            finally
            {
                heartbeatCancel.Cancel();
               try { await heartbeat; } catch (OperationCanceledException) { }
            }
            using var complete = await http.PostAsJsonAsync("api/worker/complete", report, ct);
           complete.EnsureSuccessStatusCode();
        }
    }
}
