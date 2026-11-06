using Microsoft.Data.Sqlite;

namespace Factory;

public sealed class ProjectDraftGoalInput
{
    public Guid? ProjectId { get; set; }
    public string Objective { get; set; } = "";
    public List<string> AcceptanceCriteria { get; set; } = [];
    public long? TokenBudget { get; set; }
}

internal enum DraftGoalStatus { Saved, Unauthorized, NotFound, Forbidden, Invalid, Conflict }

internal sealed record DraftGoalResult(DraftGoalStatus Status, FactoryGoal? Goal = null, string? Error = null);

public sealed partial class StateStore
{
    private static void UpgradeProjectPlanning(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS planner_project_grants(
                subject_id TEXT NOT NULL REFERENCES viewer_subjects(id),
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                PRIMARY KEY(subject_id, project_id)
            );
            CREATE TABLE IF NOT EXISTS project_planning_audit(
                id INTEGER PRIMARY KEY, subject_id TEXT NOT NULL, project_id TEXT NOT NULL,
                target_id TEXT NOT NULL, operation TEXT NOT NULL, at TEXT NOT NULL
            );
            PRAGMA user_version=13;
            """);

    internal DraftGoalResult WriteProjectDraftGoal(string authorization, Guid projectId, Guid? goalId,
        ProjectDraftGoalInput draft)
    {
        var verifier = ReadViewerVerifier(authorization);
        if (verifier is null) return new(DraftGoalStatus.Unauthorized);

        using var db = Open();
        using var tx = db.BeginTransaction();
        var access = ReadProjectAccess(db, tx, verifier, projectId);
        if (access.SubjectId is not { } subjectId) return new(DraftGoalStatus.Unauthorized);
        if (!access.CanRead || (draft.ProjectId is { } requested && requested != projectId))
            return new(DraftGoalStatus.NotFound);

        using var project = Command(db, tx, "SELECT status FROM projects WHERE id=$project;", ("$project", projectId));
        if (project.ExecuteScalar() is not string projectStatus) return new(DraftGoalStatus.NotFound);
        var existing = goalId is { } id ? ReadDraftTarget(db, tx, projectId, id) : null;
        if (goalId.HasValue && existing is null) return new(DraftGoalStatus.NotFound);
        if (!access.CanPlan) return new(DraftGoalStatus.Forbidden);
        if (projectStatus == "archived" || (existing is not null && existing.Value.State != "draft"))
            return new(DraftGoalStatus.Conflict, Error: "Only drafts in non-archived projects can be changed.");
        if (existing is not null && existing.Value.HasJobs)
            return new(DraftGoalStatus.Conflict, Error: "Goals with work packages retain their adopted scope.");
        if (existing is not null && draft.TokenBudget is { } budget && budget != existing.Value.Budget)
            return new(DraftGoalStatus.Invalid, Error: "An existing goal budget cannot be changed through this route.");

        var input = new GoalInput
        {
            ProjectId = projectId, Objective = draft.Objective, AcceptanceCriteria = draft.AcceptanceCriteria
        };
        input.TokenBudget = existing?.Budget ?? draft.TokenBudget ?? input.TokenBudget;
        if (MissionControlManager.GoalValidationError(input) is { } validationError)
            return new(DraftGoalStatus.Invalid, Error: validationError);

        var goal = MissionControlManager.NewDraftGoal(input);
        int position;
        if (existing is { } target)
        {
            goal.Id = target.Id;
            goal.CreatedAt = target.CreatedAt;
            position = target.Position;
        }
        else
        {
            using var next = Command(db, tx, "SELECT COALESCE(MAX(position),-1)+1 FROM goals;");
            position = Convert.ToInt32(next.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        Upsert(db, tx, Entities.Single(map => map.Type == typeof(FactoryGoal)), goal, position);
        Execute(db, tx, """
            INSERT INTO project_planning_audit(subject_id, project_id, target_id, operation, at)
            VALUES($subject, $project, $target, $operation, $at);
            """, ("$subject", subjectId), ("$project", projectId), ("$target", goal.Id),
            ("$operation", goalId.HasValue ? "goal_draft_updated" : "goal_draft_created"), ("$at", DateTimeOffset.UtcNow));
        tx.Commit();
        return new(DraftGoalStatus.Saved, goal);
    }

    private static (Guid Id, int Position, string State, long Budget, DateTimeOffset CreatedAt, bool HasJobs)? ReadDraftTarget(
        SqliteConnection db, SqliteTransaction tx, Guid projectId, Guid goalId)
    {
        using var query = Command(db, tx, """
            SELECT position, state, token_budget, created_at, EXISTS(SELECT 1 FROM jobs WHERE goal_id=g.id)
            FROM goals g WHERE project_id=$project AND id=$id;
            """, ("$project", projectId), ("$id", goalId));
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        return (goalId, row.GetInt32(0), row.GetString(1), row.GetInt64(2),
            (DateTimeOffset)FromDb(row.GetString(3), typeof(DateTimeOffset))!, row.GetBoolean(4));
    }
}
