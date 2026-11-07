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

    internal FactoryPlan? ReadPlanRevision(SqliteConnection db, SqliteTransaction tx, Guid goalId)
    {
        using var query = Command(db, tx, """
            SELECT content FROM goal_plan_revisions WHERE goal_id=$goal ORDER BY id DESC LIMIT 1;
            """, ("$goal", goalId));
        return query.ExecuteScalar() is string content
            ? JsonSerializer.Deserialize<FactoryPlan>(content, Json.Options)
            : MissionControlManager.ReadLegacyPlan(this, goalId);
    }

    internal static FactoryPlan InsertPlanRevision(SqliteConnection db, SqliteTransaction tx, FactoryPlan plan)
    {
        plan.Revision = Guid.NewGuid();
        StorePlanRevision(db, tx, plan);
        return plan;
    }

    private static void StorePlanRevision(SqliteConnection db, SqliteTransaction tx, FactoryPlan plan,
        bool importLegacy = false)
    {
        Execute(db, tx, """
            INSERT INTO goal_plan_revisions(goal_id, revision, content)
            SELECT $goal, $revision, $content
            WHERE NOT $import OR NOT EXISTS (SELECT 1 FROM goal_plan_revisions WHERE goal_id=$goal);
            """, ("$goal", plan.GoalId), ("$revision", plan.Revision), ("$content", Json.Stringify(plan)),
            ("$import", importLegacy));
    }
}
