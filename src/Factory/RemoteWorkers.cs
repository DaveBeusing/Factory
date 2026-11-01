using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Factory;

public sealed class WorkerClaimRequest { public string WorkerId { get; set; } = ""; }
public sealed class WorkerClaim
{
    public string WorkerId { get; set; } = "";
    public FactoryJob Job { get; set; } = new();
    public ProjectInfo Project { get; set; } = new();
    public FactoryGoal? Goal { get; set; }
    public FactorySettings Settings { get; set; } = new();
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

    public static WorkerClaim? Claim(StateStore store, FactorySettings settings, string workerId)
    {
        if (!WorkerAuthentication.IsValidWorkerId(workerId)) return null;
        return store.Change(s =>
        {
            foreach (var expired in s.Jobs.Where(j => j.Mode == "remote" &&
                !string.IsNullOrEmpty(j.LeaseOwner) && j.LeaseUntil <= DateTimeOffset.UtcNow))
            {
                expired.State = JobStates.Blocked;
               expired.Error = "Remote worker lease expired; inspect remote branch and PR before retry";
               expired.LeaseOwner = ""; expired.LeaseUntil = null;
           }
            if (s.Paused || !settings.AllowPush) return null;
           var registered = s.Workers.FirstOrDefault(w => w.Id == workerId);
           // Worker credentials can only be used with a registered, enabled identity.
            if (registered is null || !registered.Enabled || registered.Draining) return null;
            // Apply the same mode, dependency, budget, capacity and conflict gates as local runners.
            var selected = JobScheduler.Select(s, settings.MaxParallel, JobExecutionModes.Remote,
                job => s.Projects.Any(p => p.Id == job.ProjectId && !string.IsNullOrWhiteSpace(p.Origin)));
            foreach (var job in selected)
            {
                var project = s.Projects.Single(p => p.Id == job.ProjectId);
                FactoryGoal? goalCopy = null;
                if (job.GoalId is { } goalId)
                {
                    var goal = s.Goals.Single(g => g.Id == goalId);
                    var spent = s.Jobs.Where(j => j.GoalId == goalId)
                        .Aggregate(0L, (sum, j) => checked(sum + j.TokensIn + j.TokensOut));
                    // Transfer the remaining goal allowance, including this job's prior usage.
                    var others = spent - job.TokensIn - job.TokensOut;
                    goalCopy = new FactoryGoal
                    {
                        Id = goal.Id, ProjectId = goal.ProjectId, State = goal.State,
                        Objective = goal.Objective, AcceptanceCriteria = goal.AcceptanceCriteria.ToList(),
                        TokenBudget = goal.TokenBudget - others, CreatedAt = goal.CreatedAt
                    };
                }
               job.State = JobStates.Preparing;
               job.Stage = "remote_claimed";
               job.LeaseOwner = "remote:" + workerId;
               job.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(settings.LeaseSeconds);
               job.Attempt++;
               job.UpdatedAt = DateTimeOffset.UtcNow;
               registered.Heartbeat = DateTimeOffset.UtcNow;
               s.Events.Add(new FactoryEvent { JobId = job.Id, Type = "remote_claim", Message = workerId });
               return new WorkerClaim
                {
                    WorkerId = workerId,
                    Project = project, Job = job, Goal = goalCopy,
                    Settings = new FactorySettings
                    {
                        Runtime = settings.Runtime, Model = settings.Model,
                        MaxReviewRounds = settings.MaxReviewRounds, AllowPush = true,
                        LeaseSeconds = settings.LeaseSeconds, MaxParallel = 1
                    }
                };
           }
            return null;
        });
    }

    public static bool Heartbeat(StateStore store, WorkerStatus req, FactorySettings settings)
        => store.Change(s =>
        {
            var worker = s.Workers.FirstOrDefault(w => w.Id == req.WorkerId);
           if (worker != null) worker.Heartbeat = DateTimeOffset.UtcNow;
           if (req.JobId == Guid.Empty) return worker != null;
           var j = s.Jobs.FirstOrDefault(j => j.Id == req.JobId);
           if (j == null || j.LeaseOwner != "remote:" + req.WorkerId ||
                j.LeaseUntil <= DateTimeOffset.UtcNow) return false;
           if (req.TokensIn < 0 || req.TokensOut < 0) return false;
           j.TokensIn = Math.Max(j.TokensIn, req.TokensIn);
           j.TokensOut = Math.Max(j.TokensOut, req.TokensOut);
           j.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(settings.LeaseSeconds);
           return true;
        });

    public static async Task<bool> CompleteAsync(StateStore store, WorkerStatus req,
        IWorkerResultVerifier verifier, CancellationToken ct = default)
    {
        // Worker-supplied fields cannot authorize a new claim or extend a foreign lease.
        var state = store.Read();
        var claimed = state.Jobs.FirstOrDefault(j => j.Id == req.JobId);
        if (claimed is null || claimed.Mode != JobExecutionModes.Remote ||
            claimed.LeaseOwner != "remote:" + req.WorkerId ||
            claimed.LeaseUntil <= DateTimeOffset.UtcNow ||
            !JobStates.Active.Contains(claimed.State))
            return false;
        var project = state.Projects.Single(p => p.Id == claimed.ProjectId);
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

        return store.Change(s =>
        {
            var j = s.Jobs.FirstOrDefault(x => x.Id == req.JobId);
            var currentProject = s.Projects.FirstOrDefault(p => p.Id == claimed.ProjectId);
            if (j is null || j.Mode != JobExecutionModes.Remote ||
                j.LeaseOwner != "remote:" + req.WorkerId ||
                j.LeaseUntil <= DateTimeOffset.UtcNow || !JobStates.Active.Contains(j.State) ||
                j.Attempt != claimed.Attempt || j.ProjectId != claimed.ProjectId ||
                currentProject is null ||
                // A project reconfiguration invalidates verification performed against an older policy.
                Json.Stringify(currentProject.Checks) != Json.Stringify(project.Checks) ||
                Json.Stringify(currentProject.ProtectedPaths) != Json.Stringify(project.ProtectedPaths) ||
                currentProject.Origin != project.Origin || currentProject.BaseBranch != project.BaseBranch)
                return false;

            // Token counters are observational only. They cannot affect PR authorization.
            if (req.TokensIn >= 0) j.TokensIn = Math.Max(j.TokensIn, req.TokensIn);
            if (req.TokensOut >= 0) j.TokensOut = Math.Max(j.TokensOut, req.TokensOut);
            if (req.Job != null)
            {
                if (req.Job.TokensIn >= 0) j.TokensIn = Math.Max(j.TokensIn, req.Job.TokensIn);
                if (req.Job.TokensOut >= 0) j.TokensOut = Math.Max(j.TokensOut, req.Job.TokensOut);
            }

            if (verified is not null && successReported)
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
                j.State = JobStates.Blocked; j.Stage = "remote_verification_failed";
                j.Error = failure;
                s.Events.Add(new FactoryEvent { JobId = j.Id, Type = "remote_rejected", Message = failure });
            }
            j.LeaseOwner = ""; j.LeaseUntil = null; j.UpdatedAt = DateTimeOffset.UtcNow;
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
            using var claimResponse = await http.PostAsJsonAsync("api/worker/claim", new WorkerClaimRequest { WorkerId = workerId }, ct);
           claimResponse.EnsureSuccessStatusCode();
           if (claimResponse.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                try { await http.PostAsJsonAsync("api/worker/heartbeat", new WorkerStatus { WorkerId = workerId }, ct); }
                catch (HttpRequestException) { }
                await Task.Delay(3000, ct); continue;
           }
            var claim = await claimResponse.Content.ReadFromJsonAsync<WorkerClaim>(Json.Options, ct)
                ?? throw new IOException("Empty worker claim");
            if (!JobExecutionModes.Matches(claim.Job, JobExecutionModes.Remote))
                throw new InvalidOperationException("Remote worker received a job for another execution mode");
           var jobDir = Path.Combine(workspace, claim.Job.Id.ToString("N"));
           Directory.CreateDirectory(jobDir);
           var localState = new StateStore(Path.Combine(jobDir, "state"));
           var report = new WorkerStatus { WorkerId = workerId, JobId = claim.Job.Id,
                TokensIn = claim.Job.TokensIn, TokensOut = claim.Job.TokensOut };
           using var heartbeatCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
                        }
                        using var response = await http.PostAsJsonAsync("api/worker/heartbeat",
                            report, heartbeatCancel.Token);
                        response.EnsureSuccessStatusCode();
                    }
                    catch { /* lease expiry will block unsafe replay */ }
                }
            }, heartbeatCancel.Token);
           try
            {
                Directory.CreateDirectory(jobDir);
               var repoPath = Path.Combine(jobDir, "repository");
               if (!Directory.Exists(Path.Combine(repoPath, ".git")))
                {
                    var clone = await Commands.RunAsync("git", ["clone", "--branch", claim.Project.BaseBranch,
                        "--single-branch", claim.Project.Origin, repoPath], jobDir, 300, cancellationToken: ct);
                   Commands.Require(clone, "git clone");
               }
                claim.Project.Path = repoPath;
               claim.Project.Origin = GitOperations.Origin(repoPath) ?? claim.Project.Origin;
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
               var engine = new FactoryEngine(localState, claim.Settings, executionMode: JobExecutionModes.Remote);
               await engine.RunQueueAsync(ct);
               report.Job = localState.Job(claim.Job.Id);
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
