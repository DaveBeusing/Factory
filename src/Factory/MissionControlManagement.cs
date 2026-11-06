using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

namespace Factory;

public sealed class ProjectInput
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string BaseBranch { get; set; } = "master";
    public int MaxParallel { get; set; } = 2;
    public List<CheckCommand> Checks { get; set; } = [];
    public List<string> RequiredExecutionCapabilities { get; set; } = [];
    public List<string>? ProtectedPaths { get; set; }
}
public sealed class GoalInput
{
    public Guid ProjectId { get; set; }
    public string Objective { get; set; } = "";
    public List<string> AcceptanceCriteria { get; set; } = [];
    public long TokenBudget { get; set; } = 100000;
}
public sealed class JobInput
{
    public Guid ProjectId { get; set; }
    public Guid? GoalId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Mode { get; set; } = "local";
    public string Risk { get; set; } = "medium";
    public int Priority { get; set; } = 50;
    public List<Guid> DependsOn { get; set; } = [];
    public List<string> ExpectedPaths { get; set; } = [];
    public List<string> RequiredExecutionCapabilities { get; set; } = [];
    public bool ReviewedExistingWork { get; set; }
}
public sealed class ScopeAmendmentInput
{
    public List<string> ExpectedPaths { get; set; } = [];
    public string Reason { get; set; } = "";
    public bool Reviewed { get; set; }
}

public sealed class WorkerInput
{
    public string Id { get; set; } = "";
    public string Platform { get; set; } = "remote";
}
public sealed class VerifiedWorkerCapabilityInput
{
    public string Capability { get; set; } = "";
    public string Evidence { get; set; } = "";
}
public sealed class WorkerPermissionsInput
{
    public List<Guid> ProjectIds { get; set; } = [];
    public List<VerifiedWorkerCapabilityInput> VerifiedCapabilities { get; set; } = [];
    public string Reason { get; set; } = "";
}
public sealed class WorkerPermissionsResult
{
    public string WorkerId { get; set; } = "";
    public List<Guid> ProjectIds { get; set; } = [];
    public List<string> VerifiedCapabilities { get; set; } = [];
}
public sealed class ControlInput
{
    public string Action { get; set; } = "";
    public bool Reviewed { get; set; }
    public string Reason { get; set; } = "";
}

public static class LocalJobControl
{
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> Active = new();
    public static void Register(Guid id, CancellationTokenSource cts) => Active[id] = cts;
    public static void Unregister(Guid id) => Active.TryRemove(id, out _);
    public static bool IsRegistered(Guid id) => Active.ContainsKey(id);
    public static bool Request(Guid id)
    {
        if (!Active.TryGetValue(id, out var token)) return false;
        try { token.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }
}

public static class MissionControlManager
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static bool IsActive(FactoryJob j) => JobStates.Active.Contains(j.State) &&
        !string.IsNullOrWhiteSpace(j.LeaseOwner) && j.LeaseUntil > DateTimeOffset.UtcNow;
    private static void Log(FactoryState s, Guid? job, string action, string value) =>
        s.Events.Add(new FactoryEvent { JobId = job, Type = action, Message = value });
    public static ProjectInfo CreateProject(StateStore store, ProjectInput input)
    {
        Require(!string.IsNullOrWhiteSpace(input.Name) && input.Name.Length <= 120, "Project name is required (max 120 characters)");
        Require(input.MaxParallel is >= 1 and <= 64, "Project parallel limit must be 1-64");
        Require(input.BaseBranch == "master", "Factory work packages must target master");
        var path = GitOperations.RepositoryRoot(input.Path);
        GitOperations.Git(path, "rev-parse", "--verify", "refs/heads/master");
        ValidateChecks(input.Checks);
        ValidateExecutionCapabilities(input.RequiredExecutionCapabilities);
        var p = new ProjectInfo { Name = input.Name.Trim(), Path = path, BaseBranch = input.BaseBranch,
            Origin = GitOperations.Origin(path) ?? "", MaxParallel = input.MaxParallel, Checks = input.Checks,
            RequiredExecutionCapabilities = input.RequiredExecutionCapabilities };
        if (input.ProtectedPaths != null) p.ProtectedPaths = NormalizePaths(input.ProtectedPaths);
        store.Change(s =>
        {
            Require(!s.Projects.Any(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase) || GitOperations.SamePath(x.Path, p.Path)), "Project already registered");
            s.Projects.Add(p); Log(s, null, "project_created", p.Name);
        });
        return p;
    }
    public static ProjectInfo UpdateProject(StateStore store, Guid id, ProjectInput input)
    {
        Require(!string.IsNullOrWhiteSpace(input.Name) && input.Name.Length <= 120, "Invalid project name");
        Require(input.MaxParallel is >= 1 and <= 64, "Project parallel limit must be 1-64");
        Require(input.BaseBranch == "master", "Factory work packages must target master");
        var path = GitOperations.RepositoryRoot(input.Path);
        GitOperations.Git(path, "rev-parse", "--verify", "refs/heads/master");
        ValidateChecks(input.Checks);
        ValidateExecutionCapabilities(input.RequiredExecutionCapabilities);
        return store.Change(s =>
        {
            var p = s.Projects.Single(x => x.Id == id);
            Require(p.Status != "archived", "Archived project is read-only");
            Require(!s.Jobs.Any(j => j.ProjectId == id && IsActive(j)), "Cannot change project configuration while jobs are active");
            Require(!s.Projects.Any(x => x.Id != id && (x.Name.Equals(input.Name, StringComparison.OrdinalIgnoreCase) || GitOperations.SamePath(x.Path, path))), "Project name or path already registered");
            p.Name = input.Name.Trim(); p.Path = path; p.BaseBranch = input.BaseBranch;
            p.Origin = GitOperations.Origin(path) ?? "";
            p.MaxParallel = input.MaxParallel; p.Checks = input.Checks;
            p.RequiredExecutionCapabilities = input.RequiredExecutionCapabilities;
            if (input.ProtectedPaths != null) p.ProtectedPaths = NormalizePaths(input.ProtectedPaths);
            Log(s, null, "project_updated", p.Name); return p;
        });
    }
    private static void ValidateExecutionCapabilities(List<string>? capabilities)
    {
        Require(capabilities is { Count: <= 16 } &&
            capabilities.All(WorkerAuthorization.IsKnownCapability) &&
            capabilities.Distinct(StringComparer.Ordinal).Count() == capabilities.Count,
            "Execution capabilities must be distinct supported capability names");
    }
    private static void ValidateChecks(List<CheckCommand>? checks)
    {
        Require(checks is { Count: <= 32 }, "Too many checks");
        foreach (var c in checks!)
            Require(!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Executable) && c.TimeoutSeconds is >= 1 and <= 7200,
                "Invalid check name, executable or timeout");
    }
    private static List<string> NormalizePaths(IEnumerable<string> values)
    {
        var paths = values.Select(v => v.Replace('\\', '/').Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Require(paths.All(p => !System.IO.Path.IsPathRooted(p) && !p.Contains("..", StringComparison.Ordinal)), "Protected paths must be relative");
        return paths;
    }
    public static ProjectInfo SetProjectStatus(StateStore store, Guid id, string action)
        => store.Change(s =>
        {
            var p = s.Projects.Single(x => x.Id == id);
            Require(action is "activate" or "pause" or "disable" or "archive", "Unknown action");
            Require(p.Status != "archived", "Archived project is read-only");
            if (action == "archive") Require(!s.Jobs.Any(j => j.ProjectId == id && IsActive(j)), "Cannot archive a project with running jobs");
            p.Status = action switch { "activate" => "active", "pause" => "paused", "disable" => "disabled", _ => "archived" };
            Log(s, null, "project_status", p.Name + ": " + p.Status); return p;
        });
    public static FactoryGoal CreateGoal(StateStore store, GoalInput input)
    {
        ValidateGoal(input);
        return store.Change(s =>
        {
            Require(s.Projects.Any(p => p.Id == input.ProjectId && p.Status != "archived"), "Project unavailable");
            var g = new FactoryGoal { ProjectId = input.ProjectId, Objective = input.Objective.Trim(),
                AcceptanceCriteria = input.AcceptanceCriteria, TokenBudget = input.TokenBudget };
            s.Goals.Add(g); Log(s, null, "goal_created", g.Objective); return g;
        });
    }
    public static FactoryGoal UpdateGoal(StateStore store, Guid id, GoalInput input)
    {
        ValidateGoal(input);
        return store.Change(s =>
        {
            var g = s.Goals.Single(x => x.Id == id);
            Require(g.State != "archived", "Archived goal is read-only");
            Require(g.ProjectId == input.ProjectId, "Cannot move goal to another project");
            Require(input.TokenBudget >= GoalBudgetLedger.Liability(s, id),
                "Goal budget cannot be lowered below consumed and reserved tokens");
            g.Objective = input.Objective.Trim(); g.AcceptanceCriteria = input.AcceptanceCriteria; g.TokenBudget = input.TokenBudget;
            Log(s, null, "goal_updated", g.Objective); return g;
        });
    }
    private static void ValidateGoal(GoalInput g)
    {
        Require(!string.IsNullOrWhiteSpace(g.Objective) && g.Objective.Length <= 5000, "Objective is required (max 5000 characters)");
        Require(g.TokenBudget > 0, "Token budget must be positive");
        Require(g.AcceptanceCriteria.Count <= 50, "Too many acceptance criteria");
    }
    public static FactoryGoal SetGoalStatus(StateStore store, Guid id, string action)
        => store.Change(s =>
        {
            var g = s.Goals.Single(x => x.Id == id);
            Require(action is "activate" or "pause" or "stop" or "complete" or "archive", "Unknown goal action");
            Require(g.State != "archived", "Archived goal is read-only");
            if (action is "complete" or "archive") Require(!s.Jobs.Any(j => j.GoalId == id && IsActive(j)), "Cannot finish a goal while jobs run");
            if (action == "activate") Require(g.State != "completed", "Completed goal cannot be restarted");
            g.State = action switch { "activate" => "active", "pause" => "paused", "stop" => "stopped", "complete" => "completed", _ => "archived" };
            Log(s, null, "goal_status", g.Objective + ": " + g.State); return g;
        });
    public static FactoryJob CreateJob(StateStore store, JobInput input, FactorySettings settings)
    {
        ValidateJob(input);
        Require(store.Read().Projects.Any(p => p.Id == input.ProjectId && p.Status == "active"), "Project not active");
        if (input.GoalId != null) Require(store.Read().Goals.Any(g => g.Id == input.GoalId && g.ProjectId == input.ProjectId && g.State == "active"), "Goal not active");
        var snapshot = store.Read();
        var project = snapshot.Projects.Single(p => p.Id == input.ProjectId);
        WorkPackagePreflight.RequireMaster(project);
        var preflight = WorkPackagePreflight.InspectDirectJob(snapshot, project, input.Title, input.Description);
        if (preflight is not null && !input.ReviewedExistingWork)
            throw new InvalidOperationException(
                "Inspect existing master code and open work before direct job creation; acknowledge reviewedExistingWork");
        var job = store.AddJob(new FactoryJob { ProjectId = input.ProjectId, GoalId = input.GoalId,
            Title = input.Title.Trim(), Description = input.Description.Trim(), Priority = input.Priority, Risk = input.Risk,
            Mode = input.Mode, MaxRounds = settings.MaxReviewRounds, DependsOn = input.DependsOn,
            ExpectedPaths = input.ExpectedPaths,
            RequiredExecutionCapabilities = input.RequiredExecutionCapabilities });
        if (preflight is not null)
            store.Event(job.Id, "job_preflight", "master " + preflight.BaseCommit +
                "; source candidates " + preflight.ExistingCode.Count +
                "; existing work " + preflight.ExistingJobs.Count +
                "; matching PRs " + preflight.OpenPullRequests.Count +
                "; PR inventory checked " + preflight.PullRequestsChecked);
        return job;
    }
    public static FactoryJob UpdateJob(StateStore store, Guid id, JobInput input)
    {
        ValidateJob(input);
        return store.Change(s =>
        {
            var j = s.Jobs.Single(x => x.Id == id);
            Require(j.State is JobStates.Queued or JobStates.Paused or JobStates.Failed or JobStates.Blocked or JobStates.Stopped, "Only inactive jobs can be edited");
            Require(j.ProjectId == input.ProjectId, "Cannot move job between projects");
            if (input.GoalId.HasValue) Require(s.Goals.Any(g => g.Id == input.GoalId && g.ProjectId == j.ProjectId), "Goal not in project");
            Require(input.DependsOn.All(x => x != id && s.Jobs.Any(a => a.Id == x && a.ProjectId == j.ProjectId)), "Unknown or cross-project dependency");
            Require(j.Attempt == 0 || j.ExpectedPaths.SequenceEqual(input.ExpectedPaths),
                "Executed work-package scope can only change through an explicit reviewed scope amendment");
            var before = (j.Title, j.Description, j.Priority, j.Risk, j.Mode, j.GoalId, j.DependsOn, j.ExpectedPaths, j.RequiredExecutionCapabilities);
            j.Title = input.Title.Trim(); j.Description = input.Description.Trim(); j.Priority = input.Priority;
            j.Risk = input.Risk; j.Mode = input.Mode; j.GoalId = input.GoalId;
            j.DependsOn = input.DependsOn; j.ExpectedPaths = input.ExpectedPaths;
            j.RequiredExecutionCapabilities = input.RequiredExecutionCapabilities;
            if (DependencyGraph.WouldCycle(s.Jobs))
            {
                (j.Title, j.Description, j.Priority, j.Risk, j.Mode, j.GoalId, j.DependsOn, j.ExpectedPaths, j.RequiredExecutionCapabilities) = before;
                throw new InvalidOperationException("Dependency cycle detected");
            }
            j.UpdatedAt = DateTimeOffset.UtcNow;
            Log(s, id, "job_updated", j.Title); return j;
        });
    }
    private static void ValidateJob(JobInput j)
    {
        ValidateExecutionCapabilities(j.RequiredExecutionCapabilities);
        Require(!string.IsNullOrWhiteSpace(j.Title) && j.Title.Length <= 200 && !string.IsNullOrWhiteSpace(j.Description) && j.Description.Length <= 12000,
            "Title and description required");
        Require((j.Mode is "local" or "remote") && (j.Risk is "low" or "medium" or "high"), "Invalid mode or risk");
        Require(j.Priority is >= 0 and <= 100, "Priority out of range");
        WorkPackageScope.ValidateDeclaration(j.ExpectedPaths);
    }
    // Only an administrator can approve a blocked scope expansion; it never resumes the job.
    public static FactoryJob AmendScope(StateStore store, Guid id, ScopeAmendmentInput input)
    {
        Require(input.Reviewed, "Scope amendment requires an explicit reviewed approval");
        Require(!string.IsNullOrWhiteSpace(input.Reason) && input.Reason.Trim().Length >= 10 &&
            input.Reason.Length <= 1000, "Scope amendment requires a 10-1000 character justification");
        WorkPackageScope.ValidateDeclaration(input.ExpectedPaths);
        return store.Change(state =>
        {
            var job = state.Jobs.Single(j => j.Id == id);
            Require(job.State == JobStates.Blocked && job.Stage == "scope" &&
                string.IsNullOrEmpty(job.LeaseOwner),
                "Only a blocked, unclaimed scope violation can be amended");
            var project = state.Projects.Single(p => p.Id == job.ProjectId);
            // Scope approval cannot authorize changes under protected project paths.
            GitOperations.EnforcePaths(input.ExpectedPaths, project.ProtectedPaths);
            foreach (var rule in input.ExpectedPaths)
            foreach (var protectedPath in project.ProtectedPaths)
                Require(!WorkPackageScope.Includes(rule, protectedPath),
                    "A scope amendment cannot include protected project paths");

            var proposed = new FactoryJob { ProjectId = job.ProjectId,
                ExpectedPaths = input.ExpectedPaths };
            Require(!state.Jobs.Any(other => other.Id != job.Id &&
                JobStates.Active.Contains(other.State) &&
                other.LeaseUntil > DateTimeOffset.UtcNow &&
                DependencyGraph.Conflict(proposed, other)),
                "The proposed scope conflicts with an active job; coordinate work ownership first");

            var previous = job.ExpectedPaths.ToArray();
            job.ExpectedPaths = input.ExpectedPaths.ToList();
            job.Stage = "scope_amended";
            job.Error = "";
            job.UpdatedAt = DateTimeOffset.UtcNow;
            Log(state, id, "scope_amended",
                "Previously: [" + string.Join(", ", previous) + "]; Approved: [" +
                string.Join(", ", job.ExpectedPaths) + "]; Reason: " + input.Reason.Trim());
            return job;
        });
    }

    public static FactoryJob ControlJob(StateStore store, Guid id, ControlInput input)
    {
        var action = input.Action;
        Require(action is "pause" or "stop" or "resume" or "retry", "Unknown job action");
        var active = store.Job(id);
        if (IsActive(active))
        {
            Require(active.Mode == "local" && active.LeaseOwner is { Length: > 0 } && !active.LeaseOwner.StartsWith("remote:", StringComparison.Ordinal),
                "Active remote jobs cannot be interrupted from Mission Control; drain the worker");
            Require(action is "pause" or "stop", "Active jobs can only be paused or stopped");
            var updated = store.Change(s =>
            {
                var j = s.Jobs.Single(j => j.Id == id);
                j.RequestedState = action == "pause" ? JobStates.Paused : JobStates.Stopped;
                Log(s, id, "job_control_requested", action + " " + input.Reason); return j;
            });
            if (!LocalJobControl.Request(id))
            {
                store.UpdateJob(id, j => j.RequestedState = "");
                throw new InvalidOperationException("Job is not executing in this dashboard process");
            }
            return updated;
        }
        return store.Change(s =>
        {
            var j = s.Jobs.Single(j => j.Id == id);
            if (action is "pause" or "stop")
            {
                Require(j.State == JobStates.Queued || j.State is JobStates.Paused or JobStates.Stopped, "Only queued jobs can be paused/stopped immediately");
                j.State = action == "pause" ? JobStates.Paused : JobStates.Stopped;
            }
            else
            {
                Require(j.State is JobStates.Paused or JobStates.Stopped or JobStates.Failed or JobStates.Blocked,
                    "Job is not eligible for retry or resumption");
                Require(j.Stage != "scope",
                    "Scope violation must be resolved through a reviewed contract amendment before retry");
                Require(j.Stage != "reconcile",
                    "Expired lease must pass explicit process and worktree reconciliation before retry");
                Require(!j.StartedAt.HasValue || input.Reviewed, "Inspect the existing worktree and acknowledge review before restarting");
                Require(!j.StartedAt.HasValue || j.Stage is not ("commit" or "push" or "pr") || input.Reviewed,
                    "Git side effects must be reviewed before restarting");
                j.State = JobStates.Queued; j.Error = ""; j.RequestedState = "";
            }
            j.UpdatedAt = DateTimeOffset.UtcNow;
            Log(s, id, "job_control", action + " " + input.Reason);
            return j;
        });
    }
    public static WorkerInfo RegisterWorker(StateStore store, WorkerInput input)
    {
        Require(WorkerAuthentication.IsValidWorkerId(input.Id), "Worker ID must contain 1-100 ASCII letters, digits, dots, underscores or hyphens and start with a letter or digit");
        return store.Change(s =>
        {
            var w = s.Workers.FirstOrDefault(w => w.Id == input.Id);
            if (w == null) { w = new WorkerInfo { Id = input.Id }; s.Workers.Add(w); }
            w.Platform = input.Platform.Length <= 100 ? input.Platform : "remote";
            if (w.Heartbeat == default) w.Heartbeat = DateTimeOffset.MinValue;
            // Registration alone is not proof that the remote process is connected.
            Log(s, null, "worker_registered", w.Id); return w;
        });
    }
    // Administrator-only entry point. Worker registration must never create or alter grants.
    public static WorkerPermissionsResult SetWorkerPermissions(StateStore store, string id,
        WorkerPermissionsInput input)
    {
        Require(WorkerAuthentication.IsValidWorkerId(id), "Invalid worker identity");
        Require(input.Reason is { Length: >= 8 and <= 500 } &&
            !string.IsNullOrWhiteSpace(input.Reason), "A change reason is required");
        Require(input.ProjectIds is { Count: <= 64 } &&
            input.ProjectIds.All(p => p != Guid.Empty) &&
            input.ProjectIds.Distinct().Count() == input.ProjectIds.Count,
            "Project grants must be unique valid project IDs");
        Require(input.VerifiedCapabilities is { Count: <= 16 } &&
            input.VerifiedCapabilities.All(p => p is not null &&
                WorkerAuthorization.IsKnownCapability(p.Capability) &&
                p.Evidence is { Length: >= 8 and <= 2000 } &&
                !string.IsNullOrWhiteSpace(p.Evidence)) &&
            input.VerifiedCapabilities.Select(p => p.Capability)
                .Distinct(StringComparer.Ordinal).Count() == input.VerifiedCapabilities.Count,
            "Capabilities require unique known names and administrator-verified evidence");

        return store.Change(s =>
        {
            Require(s.Workers.Any(w => w.Id == id), "Worker must be registered first");
            Require(input.ProjectIds.All(pid => s.Projects.Any(p => p.Id == pid)),
                "Unknown project grant");
            // Changing project grants must not silently renew old capability attestations.
            var previousProofs = s.WorkerCapabilityProofs
                .Where(p => p.WorkerId == id)
                .ToDictionary(p => p.Capability, StringComparer.Ordinal);
            s.WorkerProjectGrants.RemoveAll(g => g.WorkerId == id);
            s.WorkerCapabilityProofs.RemoveAll(p => p.WorkerId == id);
            foreach (var projectId in input.ProjectIds)
                s.WorkerProjectGrants.Add(new WorkerProjectGrant
                {
                    WorkerId = id, ProjectId = projectId
                });
            foreach (var capability in input.VerifiedCapabilities)
                s.WorkerCapabilityProofs.Add(new WorkerCapabilityProof
                {
                    WorkerId = id, Capability = capability.Capability,
                    Evidence = capability.Evidence.Trim(),
                    VerifiedAt = previousProofs.TryGetValue(capability.Capability, out var prior) &&
                        string.Equals(prior.Evidence, capability.Evidence.Trim(), StringComparison.Ordinal)
                        ? prior.VerifiedAt : DateTimeOffset.UtcNow
                });
            var audit = id + ": " + input.Reason.Trim() +
                " (projects=" + string.Join(",", input.ProjectIds.Select(p => p.ToString("N"))) +
                "; capabilities=" + string.Join(",", input.VerifiedCapabilities.Select(p => p.Capability)) + ")";
            Log(s, null, "worker_authorization", audit[..Math.Min(audit.Length, 2000)]);
            return new WorkerPermissionsResult
            {
                WorkerId = id, ProjectIds = input.ProjectIds.ToList(),
                VerifiedCapabilities = input.VerifiedCapabilities
                    .Select(c => c.Capability).ToList()
            };
        });
    }
    public static WorkerInfo ControlWorker(StateStore store, string id, string action)
        => store.Change(s =>
        {
            var w = s.Workers.Single(w => w.Id == id);
            Require(action is "enable" or "drain" or "disable", "Unknown worker action");
            w.Enabled = action != "disable";
            w.Draining = action == "drain";
            Log(s, null, "worker_control", id + ": " + action); return w;
        });
    public static FactoryPlan SavePlan(StateStore store, FactoryPlan plan)
    {
        Planning.Validate(plan);
        var s = store.Read();
        var goal = s.Goals.Single(g => g.Id == plan.GoalId);
        Require(goal.State == "draft" && goal.ProjectId == plan.ProjectId, "Only draft goal plans are editable");
        var p = s.Projects.Single(p => p.Id == goal.ProjectId);
        plan.Preflight = WorkPackagePreflight.Inspect(s, p, goal);
        plan.BaseCommit = plan.Preflight.BaseCommit;
        var path = Planning.PathFor(store, goal.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", Json.Stringify(plan));
        File.Move(path + ".tmp", path, overwrite: true);
        store.Event(null, "plan_saved", goal.Id.ToString());
        return plan;
    }
    public static FactoryPlan? ReadPlan(StateStore store, Guid goalId)
    {
        var file = Planning.PathFor(store, goalId);
        return File.Exists(file) ? System.Text.Json.JsonSerializer.Deserialize<FactoryPlan>(File.ReadAllText(file), Json.Options) : null;
    }
}
