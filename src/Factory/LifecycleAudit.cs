using Microsoft.Data.Sqlite;

namespace Factory;

public sealed partial class StateStore
{
    private static void UpgradeLifecycleAudit(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS lifecycle_audit (
                decision_id TEXT PRIMARY KEY NOT NULL,
                project_id TEXT NOT NULL,
                entity_kind TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                actor TEXT NOT NULL,
                previous_state TEXT NOT NULL,
                resulting_state TEXT NOT NULL,
                reason_code TEXT NOT NULL,
                reference TEXT,
                generation INTEGER,
                at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS lifecycle_audit_target ON lifecycle_audit(project_id, entity_kind, entity_id, at);
            PRAGMA user_version=19;
            """);

    internal static void RecordLifecycle(SqliteConnection db, SqliteTransaction tx, Guid projectId,
        string kind, Guid entityId, string actor, string from, string to, LifecycleReason reason,
        string? reference = null, long? generation = null, FactoryState? state = null)
    {
        var decision = Guid.NewGuid();
        Execute(db, tx, """
            INSERT INTO lifecycle_audit(decision_id,project_id,entity_kind,entity_id,actor,
                previous_state,resulting_state,reason_code,reference,generation,at)
            VALUES($decision,$project,$kind,$entity,$actor,$from,$to,$reason,$reference,$generation,$at);
            """, ("$decision", decision), ("$project", projectId), ("$kind", kind), ("$entity", entityId),
            ("$actor", actor), ("$from", from), ("$to", to), ("$reason", reason.ToString()),
            ("$reference", reference), ("$generation", generation), ("$at", DateTimeOffset.UtcNow));
        var evt = new FactoryEvent
        {
            JobId = kind == "job" ? entityId : null,
            Type = kind + "_lifecycle_transition",
            Message = Json.Stringify(new
            {
                decision, projectId, entityId, actor, generation, from, to, reason = reason.ToString(), reference
            })
        };
        if (state is null) AppendEvent(db, tx, evt.JobId, evt.Type, evt.Message);
        else state.Events.Add(evt);
    }

    internal static void RecordPlanAdoption(SqliteConnection db, SqliteTransaction tx, FactoryPlan plan, string actor)
    {
        var revision = plan.Revision?.ToString() ?? throw new InvalidOperationException("A reviewed revision is required.");
        RecordLifecycle(db, tx, plan.ProjectId, "approval", plan.GoalId, actor, "pending", "approved",
            LifecycleReason.ApprovalGranted, reference: revision);
        RecordLifecycle(db, tx, plan.ProjectId, "plan", plan.GoalId, actor, "proposed", "adopted",
            LifecycleReason.PlanAdopted, reference: revision);
    }
}
