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
    public FactorySettings Settings { get; set; } = new();
}
public sealed class WorkerStatus
{
    public string WorkerId { get; set; } = "";
    public Guid JobId { get; set; }
    public FactoryJob? Job { get; set; }
    public string Evidence { get; set; } = "";
    public string Error { get; set; } = "";
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
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 100) return null;
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
            if (registered is { Enabled: false } || registered is { Draining: true }) return null;
            var active = s.Jobs.Where(j => JobStates.Active.Contains(j.State) &&
                j.LeaseUntil > DateTimeOffset.UtcNow).ToList();
            if (active.Count >= settings.MaxParallel) return null;
            foreach (var job in s.Jobs.Where(j => j.Mode == "remote" && j.State == JobStates.Queued)
                .OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedAt))
            {
                var project = s.Projects.Single(p => p.Id == job.ProjectId);
                if (string.IsNullOrEmpty(project.Origin) || !DependencyGraph.DependenciesSatisfied(job, s.Jobs)) continue;
                if (active.Count(j => j.ProjectId == job.ProjectId) >= project.MaxParallel ||
                    active.Any(j => DependencyGraph.Conflict(j, job))) continue;
                job.State = JobStates.Preparing;
                job.Stage = "remote_claimed";
                job.LeaseOwner = "remote:" + workerId;
                job.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(settings.LeaseSeconds);
                job.Attempt++;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                s.Workers.RemoveAll(w => w.Id == workerId);
                s.Workers.Add(new WorkerInfo { Id = workerId, Platform = "remote", Enabled = registered?.Enabled ?? true,
                    Draining = registered?.Draining ?? false });
                s.Events.Add(new FactoryEvent { JobId = job.Id, Type = "remote_claim", Message = workerId });
                return new WorkerClaim
                {
                    WorkerId = workerId,
                    Project = project, Job = job,
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
            j.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(settings.LeaseSeconds);
            return true;
        });

    public static bool Complete(StateStore store, WorkerStatus req)
        => store.Change(s =>
        {
            var j = s.Jobs.FirstOrDefault(x => x.Id == req.JobId);
            if (j == null || j.LeaseOwner != "remote:" + req.WorkerId ||
                j.LeaseUntil <= DateTimeOffset.UtcNow) return false;
            if (req.Job is { State: JobStates.PrReady } finished &&
                !string.IsNullOrWhiteSpace(finished.PrUrl) && !string.IsNullOrWhiteSpace(finished.HeadSha))
            {
                j.State = JobStates.PrReady; j.Stage = "pr";
                j.Branch = finished.Branch; j.PrUrl = finished.PrUrl;
                j.HeadSha = finished.HeadSha; j.TokensIn += finished.TokensIn; j.TokensOut += finished.TokensOut;
                j.Error = "";
            }
            else { j.State = JobStates.Blocked; j.Stage = "remote_error"; j.Error = req.Error; }
            j.LeaseOwner = ""; j.LeaseUntil = null; j.UpdatedAt = DateTimeOffset.UtcNow;
            j.FinishedAt = DateTimeOffset.UtcNow;
            s.Events.Add(new FactoryEvent { JobId = j.Id, Type = "remote_complete", Message = j.State });
            return true;
        });
}

public static class RemoteWorker
{
    public static async Task RunAsync(Uri server, string token, string workspace, string workerId,
        CancellationToken ct)
    {
        if (!server.IsLoopback && server.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Remote workers require HTTPS (or loopback) for API tokens");
        using var http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Directory.CreateDirectory(workspace);
        using (var register = await http.PostAsJsonAsync("api/workers/register",
            new WorkerInput { Id = workerId, Platform = Environment.OSVersion.Platform.ToString() }, ct))
            register.EnsureSuccessStatusCode();
        while (!ct.IsCancellationRequested)
        {
            using var claimResponse = await http.PostAsJsonAsync("api/workers/claim", new WorkerClaimRequest { WorkerId = workerId }, ct);
            claimResponse.EnsureSuccessStatusCode();
            if (claimResponse.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                try { await http.PostAsJsonAsync("api/workers/heartbeat", new WorkerStatus { WorkerId = workerId }, ct); }
                catch (HttpRequestException) { }
                await Task.Delay(3000, ct); continue;
            }
            var claim = await claimResponse.Content.ReadFromJsonAsync<WorkerClaim>(Json.Options, ct)
                ?? throw new IOException("Empty worker claim");
            var jobDir = Path.Combine(workspace, claim.Job.Id.ToString("N"));
            var report = new WorkerStatus { WorkerId = workerId, JobId = claim.Job.Id };
            using var heartbeatCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = Task.Run(async () =>
            {
                while (!heartbeatCancel.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, claim.Settings.LeaseSeconds / 3)), heartbeatCancel.Token); }
                    catch (OperationCanceledException) { break; }
                    try { await http.PostAsJsonAsync("api/workers/heartbeat", report, heartbeatCancel.Token); }
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
                var localState = new StateStore(Path.Combine(jobDir, "state"));
                claim.Project.Path = repoPath;
                claim.Project.Origin = GitOperations.Origin(repoPath) ?? claim.Project.Origin;
                claim.Job.State = JobStates.Queued;
                claim.Job.DependsOn = [];
                claim.Job.LeaseOwner = ""; claim.Job.LeaseUntil = null;
                localState.Change(s =>
                {
                    if (s.Projects.All(p => p.Id != claim.Project.Id)) s.Projects.Add(claim.Project);
                    if (s.Jobs.All(j => j.Id != claim.Job.Id)) s.Jobs.Add(claim.Job);
                });
                claim.Settings.WorkspaceRoot = Path.Combine(jobDir, "worktrees");
                var engine = new FactoryEngine(localState, claim.Settings);
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
            using var complete = await http.PostAsJsonAsync("api/workers/complete", report, ct);
            complete.EnsureSuccessStatusCode();
        }
    }
}
