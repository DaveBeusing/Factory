namespace Factory;

public sealed class FactoryEngine
{
    private readonly StateStore _store;
    private readonly FactorySettings _settings;
    private readonly GitOperations _git;
    private readonly IAgentRuntime _agent;
    private readonly bool _runtimeOverridden;
    private readonly Func<string, IAgentRuntime>? _remoteProviderRuntime;
    private readonly string _executionMode;
    private readonly Func<Guid, CancellationToken, Task>? _remoteBudgetExtension;
    private readonly IReadOnlyList<IFactoryPlugin> _plugins;
    private readonly string _workerId = Environment.MachineName + "/" + Guid.NewGuid().ToString("N")[..8];

    public FactoryEngine(StateStore store, FactorySettings settings, IReadOnlyList<IFactoryPlugin>? plugins = null,
        IAgentRuntime? agent = null, string executionMode = JobExecutionModes.Local,
        Func<Guid, CancellationToken, Task>? remoteBudgetExtension = null,
        Func<string, IAgentRuntime>? remoteProviderRuntime = null)
    {
        if (!JobExecutionModes.IsSupported(executionMode))
            throw new ArgumentException("Unsupported execution mode", nameof(executionMode));
        _executionMode = executionMode;
        _remoteBudgetExtension = remoteBudgetExtension;
        _remoteProviderRuntime = remoteProviderRuntime;
        _store = store; _settings = settings;
        _git = new GitOperations(string.IsNullOrEmpty(settings.WorkspaceRoot)
            ? Path.Combine(store.Home, "worktrees") : settings.WorkspaceRoot);
        _runtimeOverridden = agent != null;
        _agent = agent ?? AgentRuntimeFactory.Create(settings);
        _plugins = plugins ?? [];
    }

    private bool ProfileAllows(FactoryState state, ProjectInfo project, FactoryJob job)
    {
        if (_executionMode != JobExecutionModes.Local) return BuilderExecutionProfile.Allows(project, job);
        var runtime = _settings.Runtime;
        if (!_runtimeOverridden)
        {
            try { runtime = ProviderRouter.EffectiveCodexRuntime(_settings, ProviderRouter.Resolve(_settings, state, project.Id, "builder")); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        }
        return BuilderExecutionProfile.AllowsLocal(project, job, runtime, _runtimeOverridden);
    }

    private bool Claim(Guid id)
    {
        return _store.Change((s, db, tx) =>
        {
            var j = s.Jobs.Single(x => x.Id == id);
           if (!JobExecutionModes.Matches(j, _executionMode) || j.State != JobStates.Queued ||
                !DependencyGraph.DependenciesSatisfied(j, s.Jobs) || s.Paused) return false;
           var selected = JobScheduler.Select(s, _settings.MaxParallel, _executionMode,
                candidate => ProfileAllows(s, s.Projects.Single(p => p.Id == candidate.ProjectId), candidate),
                reservationTokens: _settings.GoalReservationTokens);
           if (!selected.Any(x => x.Id == id)) return false;
           JobRecovery.Assign(j, _workerId, _settings.LeaseSeconds, local: true,
                state: s, reservationTokens: _settings.GoalReservationTokens);
           var runtime = _runtimeOverridden ? "custom" :
                _executionMode == JobExecutionModes.Local
                    ? ProviderRouter.EffectiveCodexRuntime(_settings, ProviderRouter.Resolve(_settings, s, j.ProjectId, "builder"))
                    : _settings.Runtime;
           StateStore.RecordExecutionProvenance(db, tx, s.Projects.Single(p => p.Id == j.ProjectId), j, runtime);
           StateStore.RecordLifecycle(db, tx, j.ProjectId, "job", id, _workerId, JobStates.Queued,
               j.State, LifecycleReason.LeaseClaimed, generation: j.ExecutionGeneration, state: s);
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

    private void EnsureOwned(Guid id, ExecutionFence fence)
    {
        var job = _store.Job(id);
        JobRecovery.Require(job, fence);
        if (_executionMode == JobExecutionModes.Local && LocalJobControl.IsProjectRequest(job.RequestedState))
            throw new OperationCanceledException("Project operator requested interruption");
    }

    private void Stage(Guid id, ExecutionFence fence, string state, string stage, string? error = null)
        => _store.AdvanceFencedStage(id, fence, _store.Job(id), state, stage, error);

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
            var snapshot = _store.Read();
            var selected = JobScheduler.Select(snapshot, _settings.MaxParallel, _executionMode,
                candidate => ProfileAllows(snapshot, snapshot.Projects.Single(p => p.Id == candidate.ProjectId), candidate),
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
        var admission = _store.Read();
        if (!ProfileAllows(admission, admission.Projects.Single(p => p.Id == claimed.ProjectId), claimed))
            throw new InvalidOperationException("The runner cannot enforce the requested execution profile.");

        if (!_store.StartBuilderClaim(id, fence))
            throw new LostExecutionLeaseException("This execution claim is stale or has already started.");
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
                var renewed = _store.RenewLease(id, fence, _settings.LeaseSeconds, out var requestedState);
                if (!renewed || (_executionMode == JobExecutionModes.Local && LocalJobControl.IsProjectRequest(requestedState)))
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
            try { _store.InterruptFencedStage(id, fence); }
            catch (LostExecutionLeaseException) { }
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
                if (JobStates.Active.Contains(job.State)) return; // Keep the lease recoverable if recording the outcome failed.
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
        IAgentRuntime RuntimeFor(string role)
        {
            var snapshot = _store.Read();
            if (role == "builder" && !ProfileAllows(snapshot, snapshot.Projects.Single(project => project.Id == p.Id),
                snapshot.Jobs.Single(job => job.Id == j.Id)))
                throw new InvalidOperationException("The builder route no longer satisfies the execution profile.");
            var selected = _runtimeOverridden || _remoteProviderRuntime is not null ? (ProviderSelection?)null :
                ProviderRouter.Select(_settings, snapshot, p.Id, role, j.Risk);
            var meter = selected is { } choice
                ? new JobProviderMeter(ProviderRouter.Resolve(_settings, snapshot, p.Id, role),
                    role, choice.Model, _settings.GoalReservationTokens)
                : null;
            return new BudgetedAgentRuntime(_remoteProviderRuntime?.Invoke(role) ?? selected?.Runtime ?? _agent, _store, id, fence, extensionPolicy,
                _remoteBudgetExtension is null ? null : token => _remoteBudgetExtension(id, token),
                _settings.GoalExtensionThresholdTokens, meter);
        }
        var runtime = RuntimeFor("builder");
        var qualityEngine = new QualityEngine(runtime,
            _runtimeOverridden && _remoteProviderRuntime is null ? null : role => RuntimeFor("review:" + role));
        var worktree = _git.Prepare(p, j);
        UpdateOwned(id, fence, x =>
        {
            x.Worktree = worktree; x.Branch = $"factory/{x.Id:N}";
            JobRecovery.Checkpoint(x, "worktree", GitOperations.Head(worktree));
        });
        foreach (var plugin in _plugins) await plugin.BeforeJobAsync(p, _store.Job(id), ct);
        var report = new QualityResult();
        for (var round = 1; round <= ReviewPolicy.MaxCycles; round++)
        {
            ct.ThrowIfCancellationRequested();
            var cycle = _store.BeginReviewCycle(id, fence, j.MaxRounds);
            if (cycle == 0) break;
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
           foreach (var plugin in _plugins) await plugin.AfterGateAsync(p, _store.Job(id),
                System.Text.Json.JsonSerializer.Deserialize<QualityResult>(Json.Stringify(report), Json.Options)!, ct);
           EnsureOwned(id, fence);
            report.ReviewFindings.AddRange(report.Findings.Select(message => new ReviewFinding("controller", message)));
           report.Cycle = cycle;
           _store.FinishReviewCycle(id, fence, cycle, report);
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
        // The controller accepts independently signed approvals after the build releases its lease.
        var outcome = _store.Job(id);
        outcome.State = JobStates.CompletedLocal;
        outcome.Stage = "await_manual_integration";
        foreach (var plugin in _plugins) await plugin.AfterJobAsync(p, outcome, ct);
        EnsureOwned(id, fence);
        Stage(id, fence, outcome.State, outcome.Stage);
    }

    public async Task<IntegrationProof> VerifyIntegrationAsync(Guid id, CancellationToken ct = default,
        IRemoteBaseReader? remote = null, IIntegrationPullRequestReader? pullRequests = null)
        => await VerifyIntegrationAsync(id, null, new GitHubWorkerResultVerifier(), ct, remote, pullRequests);

    internal async Task<IntegrationProof> VerifyIntegrationAsync(Guid id, string? pullRequestUrl,
        GitHubWorkerResultVerifier verifier, CancellationToken ct = default,
        IRemoteBaseReader? remote = null, IIntegrationPullRequestReader? pullRequests = null)
    {
        var state = _store.Read();
        var job = state.Jobs.Single(j => j.Id == id);
        var project = state.Projects.Single(p => p.Id == job.ProjectId);
        if (job.State is not (JobStates.CompletedLocal or JobStates.PrReady) || job.Integrated || job.LeaseOwner.Length != 0)
            throw new InvalidOperationException("Job is not eligible for integration verification");
        var signedRemote = job.Stage == "await_signed_remote_review";
        if (signedRemote && !_store.IsReviewApproved(id))
            throw new InvalidOperationException("An exact-commit signed remote review is required.");
        var originalPullUrl = job.PrUrl;
        var originalBranch = job.Branch;
        if (signedRemote)
        {
            if (string.IsNullOrWhiteSpace(pullRequestUrl))
                throw new InvalidOperationException("Supply the merged pull request for the signed remote commit.");
            job.PrUrl = pullRequestUrl;
            job.Branch = $"factory/{job.Id:N}";
        }
        if (job.Mode == JobExecutionModes.Remote && string.IsNullOrWhiteSpace(job.PrUrl))
            throw new InvalidOperationException("Remote integration requires a verified pull request.");

        // Never accept a local-only ancestry result for a project with a configured remote.
        var proof = await IntegrationVerifier.VerifyAsync(project, job, remote, pullRequests, ct);
        var pullEvidence = job.Mode == JobExecutionModes.Remote
            ? await verifier.VerifyMergedAsync(project, job, proof.IntegratedCommit, ct)
            : null;
        ct.ThrowIfCancellationRequested();
        var recorded = _store.Change((s, db, tx) =>
        {
            var current = s.Jobs.Single(j => j.Id == id);
            var currentProject = s.Projects.Single(p => p.Id == job.ProjectId);
            if (current.State != job.State || current.Integrated || current.LeaseOwner.Length != 0 ||
                current.Mode != job.Mode || current.ProjectId != job.ProjectId ||
                current.HeadSha != job.HeadSha || current.Branch != originalBranch ||
                current.PrUrl != originalPullUrl ||
                currentProject.Path != project.Path ||
                currentProject.Origin != project.Origin ||
                currentProject.BaseBranch != project.BaseBranch ||
                (pullEvidence is not null &&
                    (!current.ExpectedPaths.SequenceEqual(job.ExpectedPaths) ||
                     ReviewPolicy.ProjectRevision(currentProject) != ReviewPolicy.ProjectRevision(project))) ||
                (signedRemote &&
                    (current.Stage != job.Stage || current.ExecutionGeneration != job.ExecutionGeneration ||
                     current.SessionId != job.SessionId ||
                     !StateStore.IsReviewApproved(db, tx, current, currentProject))))
                return false;
            if (pullEvidence is not null)
            {
                current.PrUrl = job.PrUrl;
                current.Branch = job.Branch;
                StateStore.RecordLifecycle(db, tx, current.ProjectId, "job", id, "controller/github-required-checks",
                    current.State, current.State, LifecycleReason.RemoteResultVerified, reference: pullEvidence,
                    generation: current.ExecutionGeneration, state: s);
            }
            StateStore.RecordLifecycle(db, tx, current.ProjectId, "job", id, "integration-verifier",
                current.State, JobStates.Integrated, LifecycleReason.IntegrationVerified, reference: proof.IntegratedCommit, state: s);
            StateStore.RecordIntegratedHead(db, tx, current, proof);
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
