using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Factory;

public sealed partial class StateStore
{
    private static void UpgradePlanRevisions(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS goal_plan_revisions(
                id INTEGER PRIMARY KEY, goal_id TEXT NOT NULL REFERENCES goals(id) ON DELETE CASCADE,
                revision TEXT NOT NULL UNIQUE, content TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_goal_plan_revisions_latest ON goal_plan_revisions(goal_id,id DESC);
            """);
        using var columns = Command(db, tx, """
            SELECT COUNT(*) FROM pragma_table_info('project_planning_audit') WHERE name='plan_revision';
            """);
        if (Convert.ToInt64(columns.ExecuteScalar()) == 0)
            Execute(db, tx, """
                ALTER TABLE project_planning_audit ADD COLUMN plan_revision TEXT;
                ALTER TABLE project_planning_audit ADD COLUMN reviewed_existing_work INTEGER;
                """);
        Execute(db, tx, "PRAGMA user_version=14;");
    }

    internal FactoryPlan? ReadPlanRevision(Guid goalId)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        return ReadPlanRevision(db, tx, goalId);
    }

    internal FactoryPlan? ReadPlanRevision(SqliteConnection db, SqliteTransaction tx, Guid goalId) =>
        ReadStoredPlanRevision(db, tx, goalId) ?? MissionControlManager.ReadLegacyPlan(this, goalId);

    internal FactoryPlan? ReadStoredPlanRevision(Guid goalId)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        return ReadStoredPlanRevision(db, tx, goalId);
    }

    internal static FactoryPlan? ReadStoredPlanRevision(SqliteConnection db, SqliteTransaction tx, Guid goalId)
    {
        using var query = Command(db, tx, """
            SELECT content FROM goal_plan_revisions WHERE goal_id=$goal ORDER BY id DESC LIMIT 1;
            """, ("$goal", goalId));
        return query.ExecuteScalar() is string content
            ? JsonSerializer.Deserialize<FactoryPlan>(content, Json.Options)
            : null;
    }

    internal static bool HasAdoptedSpecification(SqliteConnection db, SqliteTransaction tx, Guid? goalId)
    {
        if (goalId is null) return false;
        using var query = Command(db, tx, """
            SELECT r.content FROM goal_plan_revisions r JOIN lifecycle_audit a ON a.reference=r.revision
            WHERE r.goal_id=$goal AND a.entity_id=r.goal_id AND a.entity_kind='plan'
                AND a.reason_code='PlanAdopted' ORDER BY r.id DESC LIMIT 1;
            """, ("$goal", goalId));
        return query.ExecuteScalar() is string content &&
            JsonSerializer.Deserialize<FactoryPlan>(content, Json.Options)?.ContractVersion == 1;
    }

    internal static void VerifySpecificationAdoption(SqliteConnection db, SqliteTransaction tx, FactoryGoal goal,
        FactoryPlan plan, IReadOnlyList<FactoryJob> jobs)
    {
        if (goal.State == "draft" && !jobs.Any(j => j.GoalId == goal.Id)) return;
        if (goal.State != "active" || plan.ContractVersion != 1 || !SpecificationContracts.CanRevise(goal, jobs))
            throw new InvalidOperationException("Existing jobs prevent specification replacement.");
        using var adopted = Command(db, tx, """
            SELECT reference FROM lifecycle_audit WHERE entity_kind='plan' AND project_id=$project
                AND entity_id=$goal AND reason_code='PlanAdopted' ORDER BY rowid DESC LIMIT 1;
            """, ("$project", goal.ProjectId), ("$goal", goal.Id));
        if (adopted.ExecuteScalar() is not string previous || previous == plan.Revision.ToString())
            throw new InvalidOperationException("A new specification revision must be reviewed before replacing jobs.");
    }

    internal static IReadOnlyList<FactoryJob> RetireSpecificationJobs(SqliteConnection db, SqliteTransaction tx,
        IReadOnlyList<FactoryJob> jobs, FactoryGoal goal, FactoryPlan plan, string actor)
    {
        var current = jobs.Where(job => job.GoalId == goal.Id && !SpecificationContracts.IsSuperseded(job)).ToArray();
        foreach (var job in current)
        {
            RequireSettledJobProviderUsage(db, tx, job.Id);
            using var reservation = Command(db, tx, """
                SELECT 1 FROM goal_budget_reservations WHERE job_id=$job AND state IN ('active','held') LIMIT 1;
                """, ("$job", job.Id));
            if (reservation.ExecuteScalar() is not null)
                throw new InvalidOperationException("Settle the previous job's goal budget before replacement.");
            RecordLifecycle(db, tx, job.ProjectId, "job", job.Id, actor, job.State, JobStates.Cancelled,
                LifecycleReason.SpecificationSuperseded, reference: plan.Revision.ToString());
            job.State = JobStates.Cancelled;
            job.Stage = "specification_superseded";
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return current;
    }

    internal static FactoryPlan InsertPlanRevision(SqliteConnection db, SqliteTransaction tx, FactoryPlan plan,
        string actor = "local-operator")
    {
        var previous = ReadStoredPlanRevision(db, tx, plan.GoalId);
        if (previous?.ContractVersion == 1 && plan.ContractVersion != 1)
            throw new ArgumentException("An existing specification cannot be downgraded to a legacy plan.");
        plan.PreviousRevision = previous?.Revision;
        plan.Changes = SpecificationContracts.Diff(previous, plan);
        plan.Revision = Guid.NewGuid();
        BindIntakePlan(db, tx, plan);
        Execute(db, tx, """
            INSERT INTO goal_plan_revisions(goal_id, revision, content)
            VALUES($goal, $revision, $content);
            """, ("$goal", plan.GoalId), ("$revision", plan.Revision), ("$content", Json.Stringify(plan)));
        if (previous?.Revision is { } prior)
        {
            RecordLifecycle(db, tx, plan.ProjectId, "plan", plan.GoalId, actor, "proposed", "superseded",
                LifecycleReason.PlanSuperseded, reference: prior.ToString());
            RecordLifecycle(db, tx, plan.ProjectId, "approval", plan.GoalId, actor, "pending", "superseded",
                LifecycleReason.ApprovalSuperseded, reference: prior.ToString());
        }
        RecordLifecycle(db, tx, plan.ProjectId, "plan", plan.GoalId, actor, "none", "proposed",
            LifecycleReason.PlanProposed, reference: plan.Revision.ToString());
        RecordLifecycle(db, tx, plan.ProjectId, "approval", plan.GoalId, actor, "none", "pending",
            LifecycleReason.ApprovalRequested, reference: plan.Revision.ToString());
        return plan;
    }
}
