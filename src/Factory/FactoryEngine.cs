namespace Factory;

public sealed class FactoryEngine
{
    private readonly StateStore _store;
    private readonly FactorySettings _settings;
    private readonly GitOperations _git;
    private readonly IAgentRuntime _agent;
    private readonly bool _runtimeOverridden;
    private readonly string _executionMode;
    private readonly IReadOnlyList<IFactoryPlugin> _plugins;
    private readonly string _workerId = Environment.MachineName + "/" + Guid.NewGuid().ToString("N")[..8];

    public FactoryEngine(StateStore store, FactorySettings settings, IReadOnlyList<IFactoryPlugin>? plugins = null,
        IAgentRuntime? agent = null, string executionMode = JobExecutionModes.Local)
    {
        if (!JobExecutionModes.IsSupported(executionMode))
            throw new ArgumentException("Unsupported execution mode", nameof(executionMode));
        _executionMode = executionMode;
        _store = store; _settings = settings;
        _git = new GitOperations(string.IsNullOrEmpty(settings.WorkspaceRoot)
            ? Path.Combine(store.Home, "worktrees") : settings.WorkspaceRoot);
        _runtimeOverridden = agent != null;
        _agent = agent ?? AgentRuntimeFactory.Create(settings);
        _plugins = plugins ?? [];
    }

    private bool Claim(Guid id)
    {
        return _store.Change(s =>
        {
            var j = s.Jobs.Single(x => x.Id == id);
           if (!JobExecutionModes.Matches(j, _executionMode) || j.State != JobStates.Queued ||
                !DependencyGraph.DependenciesSatisfied(j, s.Jobs) || s.Paused) return false;
           var selected = JobScheduler.Select(s, _settings.MaxParallel, _executionMode);
           if (!selected.Any(x => x.Id == id)) return false;
           j.State = JobStates.Preparing; j.Stage = "worktree"; j.Attempt++;
           j.LeaseOwner = _workerId;
           j.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(_settings.LeaseSeconds);
           j.StartedAt ??= DateTimeOffset.UtcNow; j.UpdatedAt = DateTimeOffset.UtcNow;
           s.Events.Add(new FactoryEvent { JobId = id, Type = "claimed", Message = _workerId });
           return true;
        });
    }

    private void Stage(Guid id, string state, string stage, string? error = null)
    {
        _store.UpdateJob(id, job =>
        {
            job.State = state; job.Stage = stage;
           if (error != null) job.Error = error;
        });
        _store.Event(id, "stage", state + "/" + stage);
    }

    public IReadOnlyList<Guid> Recover()
    {
        return _store.Change(s =>
        {
            var changed = new List<Guid>();
           foreach (var job in s.Jobs.Where(j => !string.IsNullOrEmpty(j.LeaseOwner) &&
                j.LeaseUntil <= DateTimeOffset.UtcNow))
            {
                // Commit and publish are externally visible. Never blindly replay them.
                if (job.Mode == "remote" || job.Stage is "commit" or "push" or "pr")
                {
                    job.State = JobStates.Blocked;
                   job.Error = "Interrupted during Git side effects; inspect branch and PR before retry";
               }
                else
                {
                    job.State = JobStates.Queued;
                   job.Error = "Recovered after expired worker lease; inspect existing worktree";
               }
                job.LeaseOwner = ""; job.LeaseUntil = null;
               job.UpdatedAt = DateTimeOffset.UtcNow;
               changed.Add(job.Id);
           }
            return (IReadOnlyList<Guid>)changed;
        });
    }

    public async Task RunQueueAsync(CancellationToken ct)
    {
        Recover();
        var running = new Dictionary<Guid, Task>();
        while (!ct.IsCancellationRequested)
        {
            var finished = running.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToArray();
           foreach (var id in finished)
            {
                try { await running[id]; } catch (Exception ex) { _store.Event(id, "worker_error", ex.Message); }
                running.Remove(id);
           }
            var selected = JobScheduler.Select(_store.Read(), _settings.MaxParallel, _executionMode);
           foreach (var j in selected)
            {
                if (running.ContainsKey(j.Id) || !Claim(j.Id)) continue;
               var jobCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
               LocalJobControl.Register(j.Id, jobCancel);
               running[j.Id] = Task.Run(async () =>
                {
                    try { await RunClaimedAsync(j.Id, jobCancel.Token); }
                    finally { LocalJobControl.Unregister(j.Id); jobCancel.Dispose(); }
                });
           }
            if (running.Count == 0 && selected.Count == 0) break;
           await Task.Delay(1000, ct);
        }
        await Task.WhenAll(running.Values);
    }

    public async Task RunOneAsync(Guid id, CancellationToken ct)
    {
        var job = _store.Job(id);
        if (!JobExecutionModes.Matches(job, _executionMode))
            throw new InvalidOperationException("Job execution mode does not match this runner");
        if (!Claim(id))
            throw new InvalidOperationException("Job cannot be claimed: check dependencies, queue state, parallel limits and conflicts");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        LocalJobControl.Register(id, cancel);
        try { await RunClaimedAsync(id, cancel.Token); }
        finally { LocalJobControl.Unregister(id); }
    }

    public async Task RunClaimedAsync(Guid id, CancellationToken ct)
    {
        var claimed = _store.Job(id);
        if (!JobExecutionModes.Matches(claimed, _executionMode) ||
            claimed.LeaseOwner != _workerId || claimed.LeaseUntil <= DateTimeOffset.UtcNow ||
            !JobStates.Active.Contains(claimed.State))
            throw new InvalidOperationException("Job is not claimed by this runner for its execution mode");
        using var heartbeatCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCancel.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _settings.LeaseSeconds / 3)), heartbeatCancel.Token); }
                catch (OperationCanceledException) { break; }
                _store.UpdateJob(id, j =>
                {
                    if (j.LeaseOwner == _workerId && JobStates.Active.Contains(j.State))
                        j.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(_settings.LeaseSeconds);
               });
           }
        }, heartbeatCancel.Token);
        try { await ExecuteAsync(id, ct); }
        catch (TokenBudgetExceededException ex)
        {
            Stage(id, JobStates.Blocked, "budget", ex.Message);
        }
        catch (ScopeViolationException ex)
        {
            // Keep uncommitted changes for inspection and explicit contract amendment.
            Stage(id, JobStates.Blocked, "scope", ex.Message);
            _store.Event(id, "scope_violation", ex.Message);
        }
        catch (OperationCanceledException)
        {
            var interrupted = _store.Job(id);
           if (interrupted.Stage is "commit" or "push" or "pr")
                Stage(id, JobStates.Blocked, "interrupted_git", "Git side effects were interrupted; inspect branch and PR before retry");
           else
                Stage(id, interrupted.RequestedState == JobStates.Stopped ? JobStates.Stopped : JobStates.Paused,
                    "interrupted", "Execution interrupted; inspect worktree before resuming");
        }
        catch (Exception ex)
        {
            var stage = _store.Job(id).Stage;
           Stage(id, stage is "commit" or "push" or "pr" ? JobStates.Blocked : JobStates.Failed, stage, ex.Message);
        }
        finally
        {
            heartbeatCancel.Cancel();
           try { await heartbeat; } catch (OperationCanceledException) { }
            _store.UpdateJob(id, j =>
            {
                j.LeaseOwner = ""; j.LeaseUntil = null; j.FinishedAt = DateTimeOffset.UtcNow; j.RequestedState = "";
           });
        }
    }

    private async Task ExecuteAsync(Guid id, CancellationToken ct)
    {
        var j = _store.Job(id);
        if (!JobExecutionModes.Matches(j, _executionMode))
            throw new InvalidOperationException("Job execution mode changed after claim");
        var p = _store.Read().Projects.Single(x => x.Id == j.ProjectId);
        if (p.Checks.Count == 0) throw new InvalidOperationException("Project checks missing; configure gates before execution");
        WorkPackageScope.ValidateDeclaration(j.ExpectedPaths);
        var runtime = new BudgetedAgentRuntime(
            _runtimeOverridden ? _agent : AgentRuntimeFactory.Create(_settings, j.Risk), _store, id);
        var qualityEngine = new QualityEngine(runtime);
        var worktree = _git.Prepare(p, j);
        _store.UpdateJob(id, x => { x.Worktree = worktree; x.Branch = $"factory/{x.Id:N}"; });
        foreach (var plugin in _plugins) await plugin.BeforeJobAsync(p, _store.Job(id), ct);
        var report = new QualityResult();
        for (var round = 1; round <= Math.Max(1, j.MaxRounds); round++)
        {
            ct.ThrowIfCancellationRequested();
           Stage(id, round == 1 ? JobStates.Building : JobStates.Reworking, "agent");
           var existing = _store.Job(id);
           var description = round == 1 ? existing.Description :
                "Address these failed checks and reviews, and rerun necessary tests. Findings: " + string.Join("; ", report.Findings);
           var prompt = "Implement only the requested work package. Work only in this Git worktree. " +
                "Allowed paths: " + string.Join(", ", existing.ExpectedPaths) + ". " +
                "Changes outside these paths require an approved contract amendment. " +
                "Do not commit, push, merge, deploy or modify protected factory infrastructure. " +
                "Respect AGENTS.md and the existing architecture. Acceptance evidence is required. " +
                $"Work package: {existing.Title}\n{description}";
           var run = await runtime.RunAsync(worktree, prompt, existing.SessionId, ct,
                session => _store.UpdateJob(id, x => x.SessionId = session));
            ct.ThrowIfCancellationRequested();
           if (!run.Success) throw new InvalidOperationException("Agent failed: " + run.Error);
            // Only commit after the deterministic protected-path check for changed working files.
            var status = Commands.Require(Commands.Run("git", ["-C", worktree, "status", "--porcelain"], Environment.CurrentDirectory), "git status");
           if (string.IsNullOrWhiteSpace(status) && round == 1)
                throw new InvalidOperationException("Builder made no file changes");
           // Verify the complete Git delta before staging or committing.
            WorkPackageScope.ValidateWorktree(p, _store.Job(id));
           ct.ThrowIfCancellationRequested();
           Stage(id, JobStates.Gating, "commit");
           var sha = GitOperations.CommitIfNeeded(worktree, existing.Title);
           _store.UpdateJob(id, x => x.HeadSha = sha);
           ct.ThrowIfCancellationRequested();
           Stage(id, JobStates.Gating, "checks");
           report = await qualityEngine.GateAsync(p, _store.Job(id), ct);
           if (report.Passed)
            {
                Stage(id, JobStates.Reviewing, "review");
               report = await qualityEngine.ReviewAsync(p, _store.Job(id), report, ct);
           }
            ct.ThrowIfCancellationRequested();
            // Checks and reviewers can also modify files; recheck the full worktree.
            WorkPackageScope.ValidateWorktree(p, _store.Job(id));
           foreach (var plugin in _plugins) await plugin.AfterGateAsync(p, _store.Job(id), report, ct);
           var evidence = QualityEngine.Evidence(_store, _store.Job(id), report);
           _store.UpdateJob(id, x => x.EvidenceFile = evidence);
           if (report.Passed) break;
           _store.Event(id, "rework", string.Join("; ", report.Findings));
        }
        if (!report.Passed)
        {
            Stage(id, JobStates.Blocked, "quality", "Quality gate did not pass within allotted rounds");
           return;
        }
        ct.ThrowIfCancellationRequested();
        WorkPackageScope.ValidateWorktree(p, _store.Job(id));
        if (!string.IsNullOrWhiteSpace(GitOperations.Git(worktree, "status", "--porcelain")))
            throw new ScopeViolationException(
                "Worktree changed after quality verification; inspect and revalidate before integration");
        if (GitOperations.Head(worktree) != report.HeadSha)
            throw new ScopeViolationException("Commit changed after quality verification");
        Stage(id, JobStates.AwaitingApproval, "integration");
        // Push is a side effect requiring explicit operator opt-in in settings plus remote mode.
        if (j.Mode == "remote" && _settings.AllowPush)
        {
            ct.ThrowIfCancellationRequested();
           Stage(id, JobStates.AwaitingApproval, "push");
           var pr = await GitOperations.PublishAsync(p, _store.Job(id), ct);
           Stage(id, JobStates.PrReady, "pr");
           _store.UpdateJob(id, x => x.PrUrl = pr);
        }
        else Stage(id, JobStates.CompletedLocal, "await_manual_integration");
        foreach (var plugin in _plugins) await plugin.AfterJobAsync(p, _store.Job(id), ct);
    }

    public async Task<IntegrationProof> VerifyIntegrationAsync(Guid id, CancellationToken ct = default,
        IRemoteBaseReader? remote = null, IIntegrationPullRequestReader? pullRequests = null)
    {
        var state = _store.Read();
        var job = state.Jobs.Single(j => j.Id == id);
        var project = state.Projects.Single(p => p.Id == job.ProjectId);
        if (job.State is not (JobStates.CompletedLocal or JobStates.PrReady) || job.Integrated)
            throw new InvalidOperationException("Job is not eligible for integration verification");

        // Never accept a local-only ancestry result for a project with a configured remote.
        var proof = await IntegrationVerifier.VerifyAsync(project, job, remote, pullRequests, ct);
        ct.ThrowIfCancellationRequested();
        var recorded = _store.Change(s =>
        {
            var current = s.Jobs.Single(j => j.Id == id);
            var currentProject = s.Projects.Single(p => p.Id == job.ProjectId);
            if (current.State != job.State || current.Integrated ||
                current.Mode != job.Mode || current.ProjectId != job.ProjectId ||
                current.HeadSha != job.HeadSha || current.Branch != job.Branch ||
                current.PrUrl != job.PrUrl ||
                currentProject.Path != project.Path ||
                currentProject.Origin != project.Origin ||
                currentProject.BaseBranch != project.BaseBranch)
                return false;
            current.Integrated = true;
            current.State = JobStates.Integrated;
            current.Stage = "done";
            current.UpdatedAt = DateTimeOffset.UtcNow;
            s.Events.Add(new FactoryEvent
            {
                JobId = id, Type = "integrated",
                Message = $"{proof.Source}: base={proof.BaseCommit}, commit={proof.IntegratedCommit}"
            });
            return true;
        });
        if (!recorded)
            throw new InvalidOperationException("Job or project changed during integration verification; recheck required");
        return proof;
    }

}
