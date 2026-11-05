namespace Factory;

public sealed class FactoryEngine
{
    private readonly StateStore _store;
    private readonly FactorySettings _settings;
    private readonly GitOperations _git;
    private readonly IAgentRuntime _agent;
    private readonly bool _runtimeOverridden;
    private readonly string _executionMode;
    private readonly Func<Guid, CancellationToken, Task>? _remoteBudgetExtension;
    private readonly IReadOnlyList<IFactoryPlugin> _plugins;
    private readonly string _workerId = Environment.MachineName + "/" + Guid.NewGuid().ToString("N")[..8];

    public FactoryEngine(StateStore store, FactorySettings settings, IReadOnlyList<IFactoryPlugin>? plugins = null,
        IAgentRuntime? agent = null, string executionMode = JobExecutionModes.Local,
        Func<Guid, CancellationToken, Task>? remoteBudgetExtension = null)
    {
        if (!JobExecutionModes.IsSupported(executionMode))
            throw new ArgumentException("Unsupported execution mode", nameof(executionMode));
        _executionMode = executionMode;
        _remoteBudgetExtension = remoteBudgetExtension;
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
           var selected = JobScheduler.Select(s, _settings.MaxParallel, _executionMode,
                reservationTokens: _settings.GoalReservationTokens);
           if (!selected.Any(x => x.Id == id)) return false;
           JobRecovery.Assign(j, _workerId, _settings.LeaseSeconds, local: true,
                state: s, reservationTokens: _settings.GoalReservationTokens);
           s.Events.Add(new FactoryEvent { JobId = id, Type = "claimed", Message = _workerId });
           return true;
        });
    }

    private void UpdateOwned(Guid id, ExecutionFence fence, Action<FactoryJob> change)
    {
        _store.Change(state =>
        {
            var job = state.Jobs.Single(j => j.Id == id);
            JobRecovery.Require(job, fence);
            change(job);
            job.UpdatedAt = DateTimeOffset.UtcNow;
        });
    }

    private void EnsureOwned(Guid id, ExecutionFence fence) =>
        JobRecovery.Require(_store.Job(id), fence);

    private void Stage(Guid id, ExecutionFence fence, string state, string stage, string? error = null)
        => _store.SetFencedStage(id, fence, state, stage, error);

    public IReadOnlyList<Guid> Recover() => JobRecovery.QuarantineExpired(_store);

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
            var selected = JobScheduler.Select(_store.Read(), _settings.MaxParallel, _executionMode,
                reservationTokens: _settings.GoalReservationTokens);
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
        var fence = new ExecutionFence(claimed.ExecutionGeneration, claimed.LeaseToken, _workerId);
        if (!JobExecutionModes.Matches(claimed, _executionMode))
            throw new InvalidOperationException("Job is not claimed by this runner for its execution mode");
        JobRecovery.Require(claimed, fence);

        using var executionCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeatCancel = CancellationTokenSource.CreateLinkedTokenSource(executionCancel.Token);
        var heartbeat = Task.Run(async () =>
        {
            while (!heartbeatCancel.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _settings.LeaseSeconds / 3)),
                        heartbeatCancel.Token);
                }
                catch (OperationCanceledException) { break; }
                var renewed = _store.RenewLease(id, fence, _settings.LeaseSeconds);
                if (!renewed)
                {
                    executionCancel.Cancel();
                    break;
                }
            }
        }, heartbeatCancel.Token);
        try { await ExecuteAsync(id, fence, executionCancel.Token); }
        catch (LostExecutionLeaseException)
        {
            // The previous generation may not publish an outcome.
        }
        catch (TokenBudgetExceededException ex)
        {
            if (JobRecovery.Matches(_store.Job(id), fence))
                Stage(id, fence, JobStates.Blocked, "budget", ex.Message);
        }
        catch (ScopeViolationException ex)
        {
            if (JobRecovery.Matches(_store.Job(id), fence))
            {
                Stage(id, fence, JobStates.Blocked, "scope", ex.Message);
                _store.Event(id, "scope_violation", ex.Message);
            }
        }
        catch (OperationCanceledException)
        {
            var interrupted = _store.Job(id);
            if (JobRecovery.Matches(interrupted, fence))
            {
                if (interrupted.Stage is "commit" or "push" or "pr")
                    Stage(id, fence, JobStates.Blocked, "interrupted_git",
                        "Git side effects interrupted; reconcile before retry");
                else
                    Stage(id, fence,
                        interrupted.RequestedState == JobStates.Stopped ? JobStates.Stopped : JobStates.Paused,
                        "interrupted", "Execution interrupted; inspect worktree before resuming");
            }
        }
        catch (Exception ex)
        {
            var current = _store.Job(id);
            if (JobRecovery.Matches(current, fence))
                Stage(id, fence, current.Stage is "commit" or "push" or "pr" ? JobStates.Blocked : JobStates.Failed,
                    current.Stage, ex.Message);
        }
        finally
        {
            executionCancel.Cancel();
            heartbeatCancel.Cancel();
            try { await heartbeat; } catch (OperationCanceledException) { }
            _store.Change(state =>
            {
                var job = state.Jobs.Single(j => j.Id == id);
                if (job.ExecutionGeneration != fence.Generation ||
                    job.LeaseToken != fence.Token || job.LeaseOwner != fence.Owner ||
                    job.LeaseUntil <= DateTimeOffset.UtcNow)
                    return;
                GoalBudgetLedger.Settle(state, job);
                job.LeaseOwner = "";
                job.LeaseToken = "";
                job.LeaseUntil = null;
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.RequestedState = "";
            });
        }
    }

    private async Task ExecuteAsync(Guid id, ExecutionFence fence, CancellationToken ct)
    {
        var j = _store.Job(id);
        EnsureOwned(id, fence);
        if (!JobExecutionModes.Matches(j, _executionMode))
            throw new InvalidOperationException("Job execution mode changed after claim");
        var p = _store.Read().Projects.Single(x => x.Id == j.ProjectId);
        if (p.Checks.Count == 0) throw new InvalidOperationException("Project checks missing; configure gates before execution");
        WorkPackageScope.ValidateDeclaration(j.ExpectedPaths);
        // Only the controller-local runner may allocate additional goal capacity.
        // Remote workers must receive an explicit controller grant before their cap changes.
        GoalBudgetExtensionPolicy? extensionPolicy = _executionMode == JobExecutionModes.Local
            ? new(_settings.GoalExtensionIncrementTokens, _settings.GoalExtensionThresholdTokens,
                _settings.MaxJobReservationTokens)
            : null;
        var routes = _runtimeOverridden ? Array.Empty<ProjectProviderRoute>() :
            _store.GetProviderRoutes(p.Id);
        SelectedProvider? builderRoute = _runtimeOverridden ? null :
            ProviderRouting.Select(_settings, routes, p.Id, "implementation", j.Risk);
        var runtime = new BudgetedAgentRuntime(
            _runtimeOverridden ? _agent : ProviderRouting.Create(_settings, builderRoute!,
                _executionMode == JobExecutionModes.Remote),
            _store, id, fence, extensionPolicy,
            _remoteBudgetExtension is null ? null : token => _remoteBudgetExtension(id, token),
            _settings.GoalExtensionThresholdTokens, builderRoute, _settings.GoalReservationTokens);
        IAgentRuntime ReviewRuntime(string role)
        {
            var route = ProviderRouting.Select(_settings, routes, p.Id, "review:" + role, j.Risk);
            return new BudgetedAgentRuntime(ProviderRouting.Create(_settings, route,
                    _executionMode == JobExecutionModes.Remote),
                _store, id, fence, extensionPolicy,
                _remoteBudgetExtension is null ? null : token => _remoteBudgetExtension(id, token),
                _settings.GoalExtensionThresholdTokens, route, _settings.GoalReservationTokens);
        }
        var qualityEngine = new QualityEngine(runtime, _runtimeOverridden ? null : ReviewRuntime);
        var worktree = _git.Prepare(p, j);
        UpdateOwned(id, fence, x =>
        {
            x.Worktree = worktree; x.Branch = $"factory/{x.Id:N}";
            JobRecovery.Checkpoint(x, "worktree", GitOperations.Head(worktree));
        });
        foreach (var plugin in _plugins) await plugin.BeforeJobAsync(p, _store.Job(id), ct);
        var report = new QualityResult();
        for (var round = 1; round <= Math.Max(1, j.MaxRounds); round++)
        {
            ct.ThrowIfCancellationRequested();
           Stage(id, fence,round == 1 ? JobStates.Building : JobStates.Reworking, "agent");
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
                session => UpdateOwned(id, fence, x => x.SessionId = session));
            ct.ThrowIfCancellationRequested();
           EnsureOwned(id, fence);
            if (!run.Success) throw new InvalidOperationException("Agent failed: " + run.Error);
            // Only commit after the deterministic protected-path check for changed working files.
            var status = Commands.Require(Commands.Run("git", ["-C", worktree, "status", "--porcelain"], Environment.CurrentDirectory), "git status");
           if (string.IsNullOrWhiteSpace(status) && round == 1)
                throw new InvalidOperationException("Builder made no file changes");
           // Verify the complete Git delta before staging or committing.
            WorkPackageScope.ValidateWorktree(p, _store.Job(id));
           ct.ThrowIfCancellationRequested();
           Stage(id, fence,JobStates.Gating, "commit");
           EnsureOwned(id, fence);
            var sha = GitOperations.CommitIfNeeded(worktree, existing.Title);
            UpdateOwned(id, fence, x =>
            {
                x.HeadSha = sha;
                JobRecovery.Checkpoint(x, "commit", sha);
            });
           ct.ThrowIfCancellationRequested();
           Stage(id, fence,JobStates.Gating, "checks");
           report = await qualityEngine.GateAsync(p, _store.Job(id), ct);
            EnsureOwned(id, fence);
           if (report.Passed)
            {
                Stage(id, fence,JobStates.Reviewing, "review");
               report = await qualityEngine.ReviewAsync(p, _store.Job(id), report, ct);
               EnsureOwned(id, fence);
           }
            ct.ThrowIfCancellationRequested();
            // Checks and reviewers can also modify files; recheck the full worktree.
            WorkPackageScope.ValidateWorktree(p, _store.Job(id));
           foreach (var plugin in _plugins) await plugin.AfterGateAsync(p, _store.Job(id), report, ct);
           EnsureOwned(id, fence);
            var evidence = QualityEngine.Evidence(_store, _store.Job(id), report);
            UpdateOwned(id, fence, x => x.EvidenceFile = evidence);
           if (report.Passed) break;
           _store.Event(id, "rework", string.Join("; ", report.Findings));
        }
        if (!report.Passed)
        {
            Stage(id, fence,JobStates.Blocked, "quality", "Quality gate did not pass within allotted rounds");
           return;
        }
        ct.ThrowIfCancellationRequested();
        EnsureOwned(id, fence);
        WorkPackageScope.ValidateWorktree(p, _store.Job(id));
        if (!string.IsNullOrWhiteSpace(GitOperations.Git(worktree, "status", "--porcelain")))
            throw new ScopeViolationException(
                "Worktree changed after quality verification; inspect and revalidate before integration");
        if (GitOperations.Head(worktree) != report.HeadSha)
            throw new ScopeViolationException("Commit changed after quality verification");
        Stage(id, fence,JobStates.AwaitingApproval, "integration");
        // Push is a side effect requiring explicit operator opt-in in settings plus remote mode.
        if (j.Mode == "remote" && _settings.AllowPush)
        {
            ct.ThrowIfCancellationRequested();
           Stage(id, fence,JobStates.AwaitingApproval, "push");
           EnsureOwned(id, fence);
            var pr = await GitOperations.PublishAsync(p, _store.Job(id), ct);
            EnsureOwned(id, fence);
            UpdateOwned(id, fence, x => x.PrUrl = pr);
            Stage(id, fence, JobStates.PrReady, "pr");
        }
        else Stage(id, fence,JobStates.CompletedLocal, "await_manual_integration");
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
