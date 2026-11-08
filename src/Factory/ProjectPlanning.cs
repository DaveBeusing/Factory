using Microsoft.Data.Sqlite;

namespace Factory;

public sealed class ProjectDraftGoalInput
{
    public Guid? ProjectId { get; set; }
    public string Objective { get; set; } = "";
    public List<string> AcceptanceCriteria { get; set; } = [];
    public long? TokenBudget { get; set; }
    public string? GoalRevision { get; set; }
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
        ProjectDraftGoalInput draft, ReviewGoalInput? review = null)
    {
        var verifier = ReadViewerVerifier(authorization);
        if (verifier is null) return new(DraftGoalStatus.Unauthorized);

        using var db = Open();
        // Reject denied requests on a read snapshot before competing for the writer lock.
        using (var read = db.BeginTransaction(deferred: true))
        {
            if (CheckDraftAccess(db, read, verifier, projectId, goalId, draft.ProjectId, out _) is { } denied)
                return new(denied);
        }

        // The credential, grant and target can change before the write transaction starts.
        using var tx = db.BeginTransaction();
        if (CheckDraftAccess(db, tx, verifier, projectId, goalId, draft.ProjectId, out var subjectId) is { } denial)
            return new(denial);

        using var project = Command(db, tx, "SELECT status FROM projects WHERE id=$project;", ("$project", projectId));
        if (project.ExecuteScalar() is not string projectStatus) return new(DraftGoalStatus.NotFound);
        var existing = goalId is { } id ? ReadDraftTarget(db, tx, projectId, id) : null;
        if (goalId.HasValue && existing is null) return new(DraftGoalStatus.NotFound);
        if (projectStatus == "archived" || (existing is not null && existing.Value.State != "draft"))
            return new(DraftGoalStatus.Conflict, Error: "Only drafts in non-archived projects can be changed.");
        if (existing is not null && existing.Value.HasJobs)
            return new(DraftGoalStatus.Conflict, Error: "Goals with work packages retain their adopted scope.");
        if (existing is not null && draft.TokenBudget is { } budget && budget != existing.Value.Budget)
            return new(DraftGoalStatus.Invalid, Error: "An existing goal budget cannot be changed through this route.");
        if (existing is { } current)
        {
            if (string.IsNullOrWhiteSpace(draft.GoalRevision))
                return new(DraftGoalStatus.Invalid, Error: "Include the revision of the draft goal you opened.");
            var currentGoal = new FactoryGoal
            {
                Objective = current.Objective,
                AcceptanceCriteria = ReadPlanningGoalCriteria(db, tx, current.Id)
            };
            if (draft.GoalRevision != Planning.GoalRevision(currentGoal))
                return new(DraftGoalStatus.Conflict, Error: "The draft goal changed. Reopen it before saving.");
        }

        var finding = review is null ? null : ResolveScopedReviewFinding(db, tx, projectId, review);
        var input = new GoalInput
        {
            ProjectId = projectId,
            Objective = finding is not null && string.IsNullOrWhiteSpace(draft.Objective) ? finding.ProposedGoal : draft.Objective,
            AcceptanceCriteria = finding is not null && draft.AcceptanceCriteria is not { Count: > 0 }
                ? finding.AcceptanceCriteria : draft.AcceptanceCriteria
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
        if (review is not null)
            AppendEvent(db, tx, null, "review_goal_created",
                $"Project {projectId} review {review.ReviewId} finding {review.FindingId} -> goal {goal.Id}");
        tx.Commit();
        return new(DraftGoalStatus.Saved, goal);
    }

    private static DraftGoalStatus? CheckDraftAccess(SqliteConnection db, SqliteTransaction tx, string verifier,
        Guid projectId, Guid? goalId, Guid? requestedProjectId, out Guid subjectId)
    {
        var access = ReadProjectAccess(db, tx, verifier, projectId);
        subjectId = access.SubjectId.GetValueOrDefault();
        if (access.SubjectId is null) return DraftGoalStatus.Unauthorized;
        if (!access.CanRead || (requestedProjectId.HasValue && requestedProjectId != projectId))
            return DraftGoalStatus.NotFound;
        if (goalId is { } id)
        {
            using var target = Command(db, tx, "SELECT 1 FROM goals WHERE project_id=$project AND id=$id;",
                ("$project", projectId), ("$id", id));
            if (target.ExecuteScalar() is null) return DraftGoalStatus.NotFound;
        }
        return access.CanPlan ? null : DraftGoalStatus.Forbidden;
    }

    private static List<string> ReadPlanningGoalCriteria(SqliteConnection db, SqliteTransaction tx, Guid goalId)
    {
        using var criteria = Command(db, tx, """
            SELECT value FROM goals_acceptance_criteria WHERE owner_id=$goal ORDER BY position;
            """, ("$goal", goalId));
        using var values = criteria.ExecuteReader();
        var result = new List<string>();
        while (values.Read()) result.Add(values.GetString(0));
        return result;
    }

    private static (Guid Id, int Position, string State, long Budget, DateTimeOffset CreatedAt, bool HasJobs, string Objective)? ReadDraftTarget(
        SqliteConnection db, SqliteTransaction tx, Guid projectId, Guid goalId)
    {
        using var query = Command(db, tx, """
            SELECT position, state, token_budget, created_at, EXISTS(SELECT 1 FROM jobs WHERE goal_id=g.id), objective
            FROM goals g WHERE project_id=$project AND id=$id;
            """, ("$project", projectId), ("$id", goalId));
        using var row = query.ExecuteReader();
        if (!row.Read()) return null;
        return (goalId, row.GetInt32(0), row.GetString(1), row.GetInt64(2),
            (DateTimeOffset)FromDb(row.GetString(3), typeof(DateTimeOffset))!, row.GetBoolean(4), row.GetString(5));
    }
}
