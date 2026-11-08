using Microsoft.Data.Sqlite;

namespace Factory;

internal sealed class ScopedProjectRejected(DraftGoalStatus status, string? message = null) : Exception(message)
{
    internal DraftGoalStatus Status { get; } = status;
}

internal sealed record ScopedPlanView(string GoalRevision, FactoryPlan? Plan, string Objective,
    List<string> AcceptanceCriteria, string State, bool IsLegacy);

public sealed class ScopedPlanGenerationInput
{
    public string GoalRevision { get; set; } = "";
    public Guid? Revision { get; set; }
}

public sealed partial class StateStore
{
    private sealed record PlanScope(Guid SubjectId, ProjectInfo Project, FactoryGoal Goal,
        FactoryPlan? StoredPlan, FactoryPlan? LegacyPlan)
    {
        public FactoryPlan? Plan => StoredPlan ?? LegacyPlan;
    }

    internal ScopedPlanView ReadScopedPlan(string authorization, Guid projectId, Guid goalId)
    {
        var verifier = RequirePlanVerifier(authorization);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var scope = ReadPlanScope(db, tx, verifier, projectId, goalId);
        return new(Planning.GoalRevision(scope.Goal), scope.Plan, scope.Goal.Objective, scope.Goal.AcceptanceCriteria,
            scope.Goal.State, scope.StoredPlan is null && scope.LegacyPlan is not null);
    }

    internal FactoryPlan SaveScopedPlan(string authorization, Guid projectId, Guid goalId, FactoryPlan draft)
    {
        var verifier = RequirePlanVerifier(authorization);
        using var db = Open();
        var (scope, snapshot) = ReadPlanPreflight(db, verifier, projectId, goalId,
            draft.ProjectId == Guid.Empty ? null : draft.ProjectId);
        if (draft.GoalId != Guid.Empty && draft.GoalId != goalId) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        VerifyPlanSave(scope, draft);
        Planning.Validate(draft);
        draft.ProjectId = projectId;
        draft.GoalId = goalId;
        draft.Preflight = WorkPackagePreflight.Inspect(snapshot, scope.Project, scope.Goal);
        WorkPackagePreflight.RequireCurrentMaster(draft.Preflight.RemoteMasterStatus);
        draft.BaseCommit = draft.Preflight.BaseCommit;
        return CommitScopedPlan(db, verifier, scope, draft, "plan_revision_saved");
    }

    internal async Task<FactoryPlan> GenerateScopedPlanAsync(string authorization, Guid projectId, Guid goalId,
        ScopedPlanGenerationInput input, FactorySettings settings, CancellationToken cancellation)
    {
        var verifier = RequirePlanVerifier(authorization);
        using var db = Open();
        PlanScope scope;
        FactoryState snapshot;
        long grantRevision;
        var expected = new FactoryPlan { GoalRevision = input.GoalRevision, Revision = input.Revision };
        using (var read = db.BeginTransaction(deferred: true))
        {
            (scope, snapshot) = ReadPlanPreflight(db, read, verifier, projectId, goalId);
            grantRevision = ReadPlannerGrantRevision(db, read, scope)
                ?? throw new ScopedProjectRejected(DraftGoalStatus.Forbidden);
            VerifyGenerationTarget(db, read, scope);
            snapshot.ProviderAssignments = ReadViewerRows(db, read, projectId, """
                SELECT provider_id FROM provider_assignments WHERE project_id=$project AND role='planner';
                """, row => new ProviderAssignment { ProjectId = projectId, Role = "planner", ProviderId = row.GetString(0) });
        }

        var preflight = Planning.PrepareDraft(snapshot, scope.Project, scope.Goal);
        var route = ProviderRouter.Select(settings, snapshot, projectId, "planner", "medium", (profile, model, adapter) =>
            new ProjectScopedAgentRuntime(adapter, this, projectId, goalId, profile, "planner", model,
                settings.GoalReservationTokens,
                beforeReserve: () =>
                {
                    using (var read = db.BeginTransaction(deferred: true))
                        VerifyGenerationTarget(db, read, ReadPlanScope(db, read, verifier, projectId, goalId));
                    Planning.RequirePlanningCheckout(scope.Project, preflight.BaseCommit);
                },
                authorize: (connection, tx, state) =>
                {
                    VerifyGenerationTarget(connection, tx, ReadPlanScope(connection, tx, verifier, projectId, goalId));
                    var currentProfile = ProviderRouter.Resolve(settings, state, projectId, "planner");
                    if (currentProfile != profile || ProviderRouter.EffectiveModel(settings, currentProfile, "medium") != model)
                        throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The project provider route changed. Retry planning.");
                },
                auditReservation: (connection, tx, usageId) => Execute(connection, tx, """
                    INSERT INTO project_planning_audit(subject_id,project_id,target_id,operation,at)
                    VALUES($subject,$project,$usage,'plan_generation_reserved',$at);
                    """, ("$subject", scope.SubjectId), ("$project", projectId), ("$usage", usageId), ("$at", DateTimeOffset.UtcNow))));

        var plan = await Planning.GenerateDraftAsync(scope.Project, scope.Goal, preflight, route.Runtime,
            cancellation, input.Revision);
        cancellation.ThrowIfCancellationRequested();
        using (var read = db.BeginTransaction(deferred: true))
            VerifyGenerationTarget(db, read, ReadPlanScope(db, read, verifier, projectId, goalId));
        Planning.RequirePlanningCheckout(scope.Project, preflight.BaseCommit);
        return CommitScopedPlan(db, verifier, scope, plan, "plan_revision_generated", VerifyGenerationTarget, cancellation);

        void VerifyGenerationTarget(SqliteConnection connection, SqliteTransaction tx, PlanScope current)
        {
            if (current.SubjectId != scope.SubjectId || ReadPlannerGrantRevision(connection, tx, current) != grantRevision)
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The Planner grant changed. Start a new planning request.");
            VerifyPlanSave(current, expected);
            if (current.Project.Status != "active")
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Provider planning requires an active project.");
            if (current.Project.Path != scope.Project.Path || current.Project.Origin != scope.Project.Origin ||
                current.Project.BaseBranch != scope.Project.BaseBranch)
                throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The project checkout changed. Retry planning.");
        }
    }

    private static long? ReadPlannerGrantRevision(SqliteConnection db, SqliteTransaction tx, PlanScope scope)
    {
        // Each successful grant writes a new audit row in the same transaction.
        using var query = Command(db, tx, """
            SELECT id FROM viewer_access_audit
            WHERE subject_id=$subject AND project_id=$project AND operation='grant_planner'
            ORDER BY id DESC LIMIT 1;
            """, ("$subject", scope.SubjectId), ("$project", scope.Project.Id));
        return query.ExecuteScalar() as long?;
    }

    private FactoryPlan CommitScopedPlan(SqliteConnection db, string verifier, PlanScope scope, FactoryPlan draft,
        string operation, Action<SqliteConnection, SqliteTransaction, PlanScope>? verify = null,
        CancellationToken cancellation = default)
    {
        using var tx = db.BeginTransaction();
        var current = ReadPlanScope(db, tx, verifier, scope.Project.Id, scope.Goal.Id);
        VerifyPlanSave(current, draft);
        verify?.Invoke(db, tx, current);
        VerifyPlanCheckout(scope.Project, current.Project, draft.BaseCommit);
        cancellation.ThrowIfCancellationRequested();
        InsertPlanRevision(db, tx, draft);
        AuditPlan(db, tx, current, draft, operation, reviewed: null);
        AppendEvent(db, tx, null, "plan_saved", scope.Goal.Id.ToString());
        tx.Commit();
        return draft;
    }

    internal IReadOnlyList<FactoryJob> AdoptScopedPlan(string authorization, Guid projectId, Guid goalId, PlanAdoptInput input)
    {
        var verifier = RequirePlanVerifier(authorization);
        using var db = Open();
        var (scope, snapshot) = ReadPlanPreflight(db, verifier, projectId, goalId);
        RequireDraftPlanTarget(scope);
        if (scope.LegacyPlan is not null)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Save the legacy plan as a new revision and review it before adoption.");
        if (scope.StoredPlan is not { } plan || plan.Revision != input.PlanRevision || input.PlanRevision == Guid.Empty)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Review the current saved plan revision.");
        if (!input.ReviewedExistingWork)
            throw new ScopedProjectRejected(DraftGoalStatus.Invalid, "Confirm review of existing work before adoption.");
        Planning.VerifyAdoption(plan, snapshot, scope.Project, scope.Goal, input.Mode, true, input.PlanRevision);
        var jobs = Planning.BuildAdoptedJobs(plan, scope.Goal, input.Mode);

        using var tx = db.BeginTransaction();
        var current = ReadPlanScope(db, tx, verifier, projectId, goalId);
        RequireDraftPlanTarget(current);
        Planning.VerifyAdoptionTarget(plan, current.StoredPlan, scope.Project, current.Project, current.Goal);
        using var next = Command(db, tx, "SELECT COALESCE(MAX(position),-1)+1 FROM jobs;");
        var position = Convert.ToInt32(next.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        var entity = Entities.Single(map => map.Type == typeof(FactoryJob));
        foreach (var job in jobs) Upsert(db, tx, entity, job, position++);
        Execute(db, tx, "UPDATE goals SET state='active' WHERE id=$goal AND project_id=$project;",
            ("$goal", goalId), ("$project", projectId));
        AuditPlan(db, tx, current, plan, "plan_adopted", reviewed: true);
        AppendEvent(db, tx, null, "plan_adopted", $"Goal {goalId}: {jobs.Count} work packages");
        tx.Commit();
        return jobs;
    }

    private static string RequirePlanVerifier(string authorization) =>
        ReadViewerVerifier(authorization) ?? throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);

    private (PlanScope Scope, FactoryState Snapshot) ReadPlanPreflight(SqliteConnection db, string verifier,
        Guid projectId, Guid goalId, Guid? requestedProjectId = null)
    {
        using var tx = db.BeginTransaction(deferred: true);
        return ReadPlanPreflight(db, tx, verifier, projectId, goalId, requestedProjectId);
    }

    private (PlanScope Scope, FactoryState Snapshot) ReadPlanPreflight(SqliteConnection db, SqliteTransaction tx,
        string verifier, Guid projectId, Guid goalId, Guid? requestedProjectId = null)
    {
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
            throw new ScopedProjectRejected(denial);
        using var query = Command(db, tx, """
            SELECT p.path, p.origin, p.base_branch, p.status, g.objective, g.state
            FROM goals g JOIN projects p ON p.id=g.project_id
            WHERE p.id=$project AND g.id=$goal;
            """, ("$project", projectId), ("$goal", goalId));
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
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
        var stored = ReadStoredPlanRevision(db, tx, goalId);
        var legacy = stored is null ? MissionControlManager.ReadLegacyPlan(this, goalId) : null;
        var plan = stored ?? legacy;
        if (plan is not null && (plan.ProjectId != projectId || plan.GoalId != goalId))
            throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        if (plan?.Preflight is { } preflight && (preflight.ProjectId != projectId || preflight.GoalId != goalId))
            throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        return new(actor, project, goal, stored, legacy);
    }

    private static void RequireDraftPlanTarget(PlanScope scope)
    {
        if (scope.Goal.State != "draft" || scope.Project.Status == "archived")
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Only drafts in non-archived projects can be changed.");
    }

    private static void VerifyPlanSave(PlanScope scope, FactoryPlan draft)
    {
        RequireDraftPlanTarget(scope);
        if (draft.GoalRevision != Planning.GoalRevision(scope.Goal) || scope.Plan?.Revision != draft.Revision)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The goal or plan changed. Reopen the current revision.");
    }

    private static void VerifyPlanCheckout(ProjectInfo source, ProjectInfo current, string commit)
    {
        if (current.Path != source.Path || current.Origin != source.Origin ||
            WorkPackagePreflight.MasterCommit(current) != commit)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The project checkout or master changed.");
    }

    private static void AuditPlan(SqliteConnection db, SqliteTransaction tx, PlanScope scope, FactoryPlan plan,
        string operation, bool? reviewed) =>
        Execute(db, tx, """
            INSERT INTO project_planning_audit(subject_id,project_id,target_id,operation,at,plan_revision,reviewed_existing_work)
            VALUES($subject,$project,$goal,$operation,$at,$revision,$reviewed);
            """, ("$subject", scope.SubjectId), ("$project", scope.Project.Id), ("$goal", scope.Goal.Id),
            ("$operation", operation), ("$at", DateTimeOffset.UtcNow), ("$revision", plan.Revision), ("$reviewed", reviewed));
}
