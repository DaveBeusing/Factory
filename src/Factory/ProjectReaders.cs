using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Factory;

internal sealed record ViewerProject(Guid Id, string Name, string Status);
internal sealed record ViewerGoal(Guid Id, string Objective, string State, long TokenBudget);
internal sealed record ViewerJob(Guid Id, string Title, string State, Guid? GoalId);
internal sealed record ViewerUsage(string ProviderId, long InputTokens, long OutputTokens, long? ReportedCostMicros);
internal sealed record ViewerProjectSummary(ViewerProject Project, List<ViewerGoal> Goals,
    List<ViewerJob> Jobs, List<ViewerUsage> Usage);
internal sealed record ViewerJobEvidence(Guid Id, string HeadSha, string EvidenceFile);

public sealed partial class StateStore
{
    private const string ViewerTokenPrefix = "factory-viewer-";

    private static void UpgradeProjectReaders(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS viewer_subjects(
                id TEXT PRIMARY KEY, name TEXT NOT NULL, verifier TEXT UNIQUE
            );
            CREATE TABLE IF NOT EXISTS viewer_project_grants(
                subject_id TEXT NOT NULL REFERENCES viewer_subjects(id),
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                PRIMARY KEY(subject_id, project_id)
            );
            CREATE TABLE IF NOT EXISTS viewer_access_audit(
                id INTEGER PRIMARY KEY, subject_id TEXT NOT NULL,
                project_id TEXT, operation TEXT NOT NULL, actor TEXT NOT NULL, at TEXT NOT NULL
            );
            PRAGMA user_version=12;
            """);

    internal (Guid SubjectId, string Token) CreateViewer(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Viewer name is required");
        var subjectId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        using var db = Open();
        using var tx = db.BeginTransaction();
        Execute(db, tx, "INSERT INTO viewer_subjects(id, name, verifier) VALUES($id, $name, $verifier);",
            ("$id", subjectId), ("$name", name.Trim()), ("$verifier", ViewerVerifier(secret)));
        AuditViewerAccess(db, tx, subjectId, null, "create");
        tx.Commit();
        return (subjectId, ViewerTokenPrefix + Convert.ToHexString(secret));
    }

    internal string? SetViewerCredential(Guid subjectId, bool rotate)
    {
        var secret = rotate ? RandomNumberGenerator.GetBytes(32) : null;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var update = Command(db, tx, "UPDATE viewer_subjects SET verifier=$verifier WHERE id=$id;",
            ("$id", subjectId), ("$verifier", secret is null ? null : ViewerVerifier(secret)));
        if (update.ExecuteNonQuery() != 1) throw new ArgumentException("Unknown viewer subject");
        AuditViewerAccess(db, tx, subjectId, null, rotate ? "rotate" : "disable");
        tx.Commit();
        return secret is null ? null : ViewerTokenPrefix + Convert.ToHexString(secret);
    }

    internal bool SetViewerGrant(Guid subjectId, Guid projectId, bool grant)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var update = Command(db, tx, grant
            ? """
                INSERT INTO viewer_project_grants(subject_id, project_id) VALUES($subject, $project)
                ON CONFLICT(subject_id, project_id) DO NOTHING;
                """
            : "DELETE FROM viewer_project_grants WHERE subject_id=$subject AND project_id=$project;",
            ("$subject", subjectId), ("$project", projectId));
        var changed = update.ExecuteNonQuery() == 1;
        if (changed) AuditViewerAccess(db, tx, subjectId, projectId, grant ? "grant" : "revoke");
        tx.Commit();
        return changed;
    }

    private static void AuditViewerAccess(SqliteConnection db, SqliteTransaction tx, Guid subjectId,
        Guid? projectId, string operation) =>
        Execute(db, tx, """
            INSERT INTO viewer_access_audit(subject_id, project_id, operation, actor, at)
            VALUES($subject, $project, $operation, $actor, $at);
            """,
            ("$subject", subjectId), ("$project", projectId), ("$operation", operation),
            ("$actor", Environment.UserDomainName + "\\" + Environment.UserName), ("$at", DateTimeOffset.UtcNow));

    private static string ViewerVerifier(byte[] secret) => Convert.ToHexString(SHA256.HashData(secret));

    internal (bool Authenticated, ViewerProjectSummary? Value) ReadViewerSummary(string authorization, Guid projectId) =>
        ReadViewerProject<ViewerProjectSummary>(authorization, projectId, (db, tx) =>
        {
            using var query = Command(db, tx, "SELECT name, status FROM projects WHERE id=$project;",
                ("$project", projectId));
            using var reader = query.ExecuteReader();
            if (!reader.Read()) return null;
            var project = new ViewerProject(projectId, reader.GetString(0), reader.GetString(1));
            reader.Close();

            var goals = ReadViewerRows(db, tx, projectId, """
                SELECT id, objective, state, token_budget FROM goals WHERE project_id=$project ORDER BY position,id;
                """, row => new ViewerGoal(Guid.Parse(row.GetString(0)), row.GetString(1), row.GetString(2), row.GetInt64(3)));
            var jobs = ReadViewerRows(db, tx, projectId, """
                SELECT j.id, j.title, j.state, g.id
                FROM jobs j LEFT JOIN goals g ON g.id=j.goal_id AND g.project_id=j.project_id
                WHERE j.project_id=$project ORDER BY j.position,j.id;
                """, row => new ViewerJob(Guid.Parse(row.GetString(0)), row.GetString(1), row.GetString(2),
                    row.IsDBNull(3) ? null : Guid.Parse(row.GetString(3))));
            var usage = ReadViewerRows(db, tx, projectId, """
                SELECT provider_id, SUM(input_tokens), SUM(output_tokens), SUM(reported_cost_micros)
                FROM provider_usages WHERE project_id=$project
                GROUP BY provider_id ORDER BY MIN(position),provider_id;
                """, row => new ViewerUsage(row.GetString(0), row.GetInt64(1), row.GetInt64(2),
                    row.IsDBNull(3) ? null : row.GetInt64(3)));
            return new ViewerProjectSummary(project, goals, jobs, usage);
        });

    internal (bool Authenticated, ViewerJobEvidence? Value) ReadViewerJobEvidence(
        string authorization, Guid projectId, Guid jobId) =>
        ReadViewerProject<ViewerJobEvidence>(authorization, projectId, (db, tx) =>
        {
            using var query = Command(db, tx, """
                SELECT head_sha, evidence_file FROM jobs WHERE project_id=$project AND id=$job;
                """, ("$project", projectId), ("$job", jobId));
            using var reader = query.ExecuteReader();
            return reader.Read() ? new ViewerJobEvidence(jobId, reader.GetString(0), reader.GetString(1)) : null;
        });

    private static List<T> ReadViewerRows<T>(SqliteConnection db, SqliteTransaction tx, Guid projectId,
        string sql, Func<SqliteDataReader, T> projectRow)
    {
        using var query = Command(db, tx, sql, ("$project", projectId));
        using var reader = query.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(projectRow(reader));
        return rows;
    }

    private (bool Authenticated, T? Value) ReadViewerProject<T>(string authorization, Guid projectId,
        Func<SqliteConnection, SqliteTransaction, T?> read) where T : class
    {
        if (!AuthenticationHeaderValue.TryParse(authorization, out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is not { } token || !token.StartsWith(ViewerTokenPrefix, StringComparison.Ordinal) ||
            !WorkerAuthentication.IsValidKey(token[ViewerTokenPrefix.Length..]))
            return (false, null);
        var verifier = ViewerVerifier(Convert.FromHexString(token[ViewerTokenPrefix.Length..]));
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        using var subject = Command(db, tx, "SELECT id FROM viewer_subjects WHERE verifier=$verifier;",
            ("$verifier", verifier));
        if (subject.ExecuteScalar() is not string subjectId) return (false, null);
        using var grant = Command(db, tx, """
            SELECT 1 FROM viewer_project_grants WHERE subject_id=$subject AND project_id=$project;
            """, ("$subject", subjectId), ("$project", projectId));
        // The grant and the scoped projection share one SQLite snapshot.
        return (true, grant.ExecuteScalar() is null ? null : read(db, tx));
    }
}
