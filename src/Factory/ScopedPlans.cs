using Microsoft.Data.Sqlite;

namespace Factory;

internal sealed class ScopedPlanRejected(DraftGoalStatus status, string? message = null) : Exception(message)
{
    internal DraftGoalStatus Status { get; } = status;
}

internal sealed record ScopedPlanView(string GoalRevision, FactoryPlan? Plan);

public sealed partial class StateStore
{
    private sealed record PlanScope(Guid SubjectId, ProjectInfo Project, FactoryGoal Goal, FactoryPlan? Plan);

    internal ScopedPlanView ReadScopedPlan(string authorization, Guid projectId, Guid goalId)
    {
        var verifier = RequirePlanVerifier(authorization);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var scope = ReadPlanScope(db, tx, verifier, projectId, goalId);
        return new(Planning.GoalRevision(scope.Goal), scope.Plan);
    }

    internal FactoryPlan SaveScopedPlan(string authorization, Guid projectId, Guid goalId, FactoryPlan draft)
    {
        var verifier = RequirePlanVerifier(authorization);
        using var db = Open();
        var (scope, snapshot) = ReadPlanPreflight(db, verifier, projectId, goalId,
            draft.ProjectId == Guid.Empty ? null : draft.ProjectId);
        if (draft.GoalId != Guid.Empty && draft.GoalId != goalId) throw new ScopedPlanRejected(DraftGoalStatus.NotFound);
        VerifyPlanSave(scope, draft);
        Planning.Validate(draft);
        draft.ProjectId = projectId;
        draft.GoalId = goalId;
        draft.Preflight = WorkPackagePreflight.Inspect(snapshot, scope.Project, scope.Goal);
        WorkPackagePreflight.RequireCurrentMaster(draft.Preflight.RemoteMasterStatus);
        draft.BaseCommit = draft.Preflight.BaseCommit;

        using var tx = db.BeginTransaction();
        var current = ReadPlanScope(db, tx, verifier, projectId, goalId);
        VerifyPlanSave(current, draft);
        VerifyPlanCheckout(scope.Project, current.Project, draft.BaseCommit);
        InsertPlanRevision(db, tx, draft);
        AuditPlan(db, tx, current, draft, "plan_revision_saved", reviewed: null);
        AppendEvent(db, tx, null, "plan_saved", goalId.ToString());
        tx.Commit();
        return draft;
    }

    internal IReadOnlyList<FactoryJob> AdoptScopedPlan(string authorization, Guid projectId, Guid goalId, PlanAdoptInput input)
    {
        var verifier = RequirePlanVerifier(authorization);
        using var db = Open();
        var (scope, snapshot) = ReadPlanPreflight(db, verifier, projectId, goalId);
        RequireDraftPlanTarget(scope);
        if (scope.Plan is not { } plan || plan.Revision != input.PlanRevision || input.PlanRevision == Guid.Empty)
            throw new ScopedPlanRejected(DraftGoalStatus.Conflict, "Review the current saved plan revision.");
        if (!input.ReviewedExistingWork)
            throw new ScopedPlanRejected(DraftGoalStatus.Invalid, "Confirm review of existing work before adoption.");
        Planning.VerifyAdoption(plan, snapshot, scope.Project, scope.Goal, input.Mode, true, input.PlanRevision);
        var jobs = Planning.BuildAdoptedJobs(plan, scope.Goal, input.Mode);

        using var tx = db.BeginTransaction();
        var current = ReadPlanScope(db, tx, verifier, projectId, goalId);
        RequireDraftPlanTarget(current);
        Planning.VerifyAdoptionTarget(plan, current.Plan, scope.Project, current.Project, current.Goal);
        using var next = Command(db, tx, "SELECT COALESCE(MAX(position),-1)+1 FROM jobs;");
        var position = Convert.ToInt32(next.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        var entity = Entities.Single(map => map.Type == typeof(FactoryJob));
        foreach (var job in jobs) Upsert(db, tx, entity, job, position++);
        Execute(db, tx, "UPDATE goals SET state='active' WHERE id=$goal AND project_id=$project;",
            ("$goal", goalId), ("$project", projectId));
        StorePlanRevision(db, tx, plan, importLegacy: true);
        AuditPlan(db, tx, current, plan, "plan_adopted", reviewed: true);
        AppendEvent(db, tx, null, "plan_adopted", $"Goal {goalId}: {jobs.Count} work packages");
        tx.Commit();
        return jobs;
    }

    private static string RequirePlanVerifier(string authorization) =>
        ReadViewerVerifier(authorization) ?? throw new ScopedPlanRejected(DraftGoalStatus.Unauthorized);

    private (PlanScope Scope, FactoryState Snapshot) ReadPlanPreflight(SqliteConnection db, string verifier,
        Guid projectId, Guid goalId, Guid? requestedProjectId = null)
    {
        using var tx = db.BeginTransaction(deferred: true);
        var scope = ReadPlanScope(db, tx, verifier, projectId, goalId, requestedProjectId);
        var jobs = ReadViewerRows(db, tx, projectId, """
            SELECT id, title, description, state, integrated, updated_at FROM jobs WHERE project_id=$project;
            """, row => new FactoryJob
            {
                Id = Guid.Parse(row.GetString(0)), ProjectId = projectId, Title = row.GetString(1),
                Description = row.GetString(2), State = row.GetString(3), Integrated = row.GetBoolean(4),
                UpdatedAt = (DateTimeOffset)FromDb(row.GetString(5), typeof(DateTimeOffset))!
            });
        return (scope, new FactoryState { Projects = [scope.Project], Jobs = jobs });
    }

    private PlanScope ReadPlanScope(SqliteConnection db, SqliteTransaction tx, string verifier, Guid projectId,
        Guid goalId, Guid? requestedProjectId = null)
    {
        if (CheckDraftAccess(db, tx, verifier, projectId, goalId, requestedProjectId, out var actor) is { } denial)
            throw new ScopedPlanRejected(denial);
        using var query = Command(db, tx, """
            SELECT p.path, p.origin, p.base_branch, p.status, g.objective, g.state
            FROM goals g JOIN projects p ON p.id=g.project_id
            WHERE p.id=$project AND g.id=$goal;
            """, ("$project", projectId), ("$goal", goalId));
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new ScopedPlanRejected(DraftGoalStatus.NotFound);
        var project = new ProjectInfo
        {
            Id = projectId, Path = row.GetString(0), Origin = row.GetString(1), BaseBranch = row.GetString(2),
            Status = row.GetString(3)
        };
        var goal = new FactoryGoal { Id = goalId, ProjectId = projectId, Objective = row.GetString(4), State = row.GetString(5) };
        row.Close();
        using var criteria = Command(db, tx, """
            SELECT value FROM goals_acceptance_criteria WHERE owner_id=$goal ORDER BY position;
            """, ("$goal", goalId));
        using var values = criteria.ExecuteReader();
        while (values.Read()) goal.AcceptanceCriteria.Add(values.GetString(0));
        values.Close();
        var plan = ReadPlanRevision(db, tx, goalId);
        if (plan is not null && (plan.ProjectId != projectId || plan.GoalId != goalId))
            throw new ScopedPlanRejected(DraftGoalStatus.NotFound);
        if (plan?.Preflight is { } preflight && (preflight.ProjectId != projectId || preflight.GoalId != goalId))
            throw new ScopedPlanRejected(DraftGoalStatus.NotFound);
        return new(actor, project, goal, plan);
    }

    private static void RequireDraftPlanTarget(PlanScope scope)
    {
        if (scope.Goal.State != "draft" || scope.Project.Status == "archived")
            throw new ScopedPlanRejected(DraftGoalStatus.Conflict, "Only drafts in non-archived projects can be changed.");
    }

    private static void VerifyPlanSave(PlanScope scope, FactoryPlan draft)
    {
        RequireDraftPlanTarget(scope);
        if (draft.GoalRevision != Planning.GoalRevision(scope.Goal) || scope.Plan?.Revision != draft.Revision)
            throw new ScopedPlanRejected(DraftGoalStatus.Conflict, "The goal or plan changed. Reopen the current revision.");
    }

    private static void VerifyPlanCheckout(ProjectInfo source, ProjectInfo current, string commit)
    {
        if (current.Path != source.Path || current.Origin != source.Origin ||
            WorkPackagePreflight.MasterCommit(current) != commit)
            throw new ScopedPlanRejected(DraftGoalStatus.Conflict, "The project checkout or master changed.");
    }

    private static void AuditPlan(SqliteConnection db, SqliteTransaction tx, PlanScope scope, FactoryPlan plan,
        string operation, bool? reviewed) =>
        Execute(db, tx, """
            INSERT INTO project_planning_audit(subject_id,project_id,target_id,operation,at,plan_revision,reviewed_existing_work)
            VALUES($subject,$project,$goal,$operation,$at,$revision,$reviewed);
            """, ("$subject", scope.SubjectId), ("$project", scope.Project.Id), ("$goal", scope.Goal.Id),
            ("$operation", operation), ("$at", DateTimeOffset.UtcNow), ("$revision", plan.Revision), ("$reviewed", reviewed));
}
