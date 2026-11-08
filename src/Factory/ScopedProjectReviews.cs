using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Factory;

public sealed partial class StateStore
{
    private static void UpgradeProjectReviews(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS scoped_project_reviews(
                project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
                subject_id TEXT NOT NULL REFERENCES viewer_subjects(id),
                review_json TEXT NOT NULL
            );
            PRAGMA user_version=18;
            """);

    internal ProjectReviewSnapshot? ReadScopedProjectReview(string authorization, Guid projectId)
    {
        var verifier = RequirePlanVerifier(authorization);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var access = ReadProjectAccess(db, tx, verifier, projectId);
        if (access.SubjectId is null) throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        if (!access.CanRead) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        return ReadStoredProjectReview(db, tx, projectId);
    }

    internal ProjectReviewSnapshot AnalyzeScopedProject(string authorization, Guid projectId, CancellationToken ct)
    {
        var verifier = RequirePlanVerifier(authorization);
        using var db = Open();
        Guid actor;
        long grant;
        ProjectInfo project;
        bool hasChecks;
        int blockedCount;
        using (var read = db.BeginTransaction(deferred: true))
        {
            actor = RequireReviewPlanner(db, read, verifier, projectId);
            grant = ReadPlannerGrantRevision(db, read, actor, projectId)
                ?? throw new ScopedProjectRejected(DraftGoalStatus.Forbidden);
            project = ReadReviewProject(db, read, projectId);
            using var stats = Command(db, read, """
                SELECT EXISTS(SELECT 1 FROM project_checks WHERE owner_id=$project),
                    (SELECT COUNT(*) FROM jobs WHERE project_id=$project AND state IN ('blocked','failed'));
                """, ("$project", projectId));
            using var row = stats.ExecuteReader();
            row.Read();
            hasChecks = row.GetBoolean(0);
            blockedCount = row.GetInt32(1);
        }
        var (review, _) = ProjectReviewer.Inspect(project, hasChecks, blockedCount, ct);
        using var tx = db.BeginTransaction();
        if (RequireReviewPlanner(db, tx, verifier, projectId) != actor ||
            ReadPlannerGrantRevision(db, tx, actor, projectId) != grant)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The Planner grant changed. Run analysis again.");
        var current = ReadReviewProject(db, tx, projectId);
        if (current.Path != project.Path || current.BaseBranch != project.BaseBranch ||
            WorkPackagePreflight.MasterCommit(current) != review.Commit)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The project or master changed. Run analysis again.");
        ct.ThrowIfCancellationRequested();
        Execute(db, tx, """
            INSERT INTO scoped_project_reviews(project_id,subject_id,review_json) VALUES($project,$subject,$review)
            ON CONFLICT(project_id) DO UPDATE SET subject_id=excluded.subject_id,review_json=excluded.review_json;
            """, ("$project", projectId), ("$subject", actor), ("$review", Json.Stringify(review)));
        AppendEvent(db, tx, null, "project_review_created", $"Project {projectId} review {review.Id} at {review.Commit}");
        tx.Commit();
        return review;
    }

    private static Guid RequireReviewPlanner(SqliteConnection db, SqliteTransaction tx, string verifier, Guid projectId)
    {
        if (CheckDraftAccess(db, tx, verifier, projectId, null, null, out var actor) is { } denial)
            throw new ScopedProjectRejected(denial);
        return actor;
    }

    private static ProjectInfo ReadReviewProject(SqliteConnection db, SqliteTransaction tx, Guid projectId)
    {
        using var query = Command(db, tx, "SELECT path,base_branch,status FROM projects WHERE id=$project;", ("$project", projectId));
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        if (row.GetString(2) == "archived")
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Archived projects cannot be analyzed or used to create goals.");
        return new ProjectInfo { Id = projectId, Path = row.GetString(0), BaseBranch = row.GetString(1) };
    }

    private static ProjectReviewSnapshot? ReadStoredProjectReview(SqliteConnection db, SqliteTransaction tx, Guid projectId)
    {
        using var query = Command(db, tx, "SELECT review_json FROM scoped_project_reviews WHERE project_id=$project;", ("$project", projectId));
        if (query.ExecuteScalar() is not string json) return null;
        var review = JsonSerializer.Deserialize<ProjectReviewSnapshot>(json, Json.Options);
        return review?.ProjectId == projectId ? review : null;
    }

    private static ProjectReviewFinding ResolveScopedReviewFinding(SqliteConnection db, SqliteTransaction tx,
        Guid projectId, ReviewGoalInput input)
    {
        if (input.ReviewId is null || input.ReviewId == Guid.Empty)
            throw new ScopedProjectRejected(DraftGoalStatus.Invalid, "Include the reviewed snapshot ID.");
        var review = ReadStoredProjectReview(db, tx, projectId);
        if (review is null || review.Id != input.ReviewId)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The review changed. Reopen the current finding.");
        var finding = review.Findings.SingleOrDefault(item => item.Id == input.FindingId)
            ?? throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        if (WorkPackagePreflight.MasterCommit(ReadReviewProject(db, tx, projectId)) != review.Commit)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "Master advanced. Run analysis again.");
        return finding;
    }
}
