using Microsoft.Data.Sqlite;

namespace Factory;

public sealed record ReviewFinding(string Role, string Message);
public sealed record ReviewRecord(string Role, string Commit, string PolicyRevision, string Adapter,
    string Session, string Scope, string Verdict, string Assurance);

internal static class ReviewPolicy
{
    internal const string Revision = "review-policy-v1";
    internal const int MaxCycles = 3;
    internal static readonly string[] Roles = ["code", "architecture", "security", "ux"];

    // Adapter output and a distinct session are evidence, but neither establishes
    // a trusted reviewer identity or isolation from the builder.
    internal static bool HasIndependentApproval(QualityResult report) =>
        report.ReviewRecords.Count == Roles.Length && Roles.All(role => report.ReviewRecords.Count(record =>
            record.Role == role && record.Commit == report.HeadSha && record.PolicyRevision == Revision &&
            record.Verdict == "PASS" && record.Assurance == "VerifiedIndependent" &&
            record.Session.Length != 0 && record.Scope == "read-only") == 1);
}

public sealed partial class StateStore
{
    private static void UpgradeReviewCycles(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS review_cycles(
                job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                cycle INTEGER NOT NULL CHECK(cycle BETWEEN 1 AND 3),
                generation INTEGER NOT NULL, head_sha TEXT, report TEXT,
                PRIMARY KEY(job_id,cycle)
            );
            PRAGMA user_version=24;
            """);

    internal static int ReviewCyclesUsed(SqliteConnection db, SqliteTransaction tx, Guid id)
    {
        using var count = Command(db, tx, "SELECT COUNT(*) FROM review_cycles WHERE job_id=$id;", ("$id", id));
        return Convert.ToInt32(count.ExecuteScalar());
    }

    private static void ReserveReviewCycle(SqliteConnection db, SqliteTransaction tx, Guid id, long generation, int cycle) =>
        Execute(db, tx, "INSERT INTO review_cycles(job_id,cycle,generation) VALUES($id,$cycle,$generation);",
            ("$id", id), ("$cycle", cycle), ("$generation", generation));

    internal int BeginReviewCycle(Guid id, ExecutionFence fence, int maximum)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var job = ReadJob(db, tx, id) ?? throw new InvalidOperationException("Job missing.");
        JobRecovery.Require(job, fence);
        using var count = Command(db, tx, "SELECT COUNT(*) FROM review_cycles WHERE job_id=$id;", ("$id", id));
        var used = Convert.ToInt32(count.ExecuteScalar());
        if (used >= Math.Clamp(maximum, 1, ReviewPolicy.MaxCycles)) return 0;
        using var pending = Command(db, tx, """
            SELECT 1 FROM review_cycles WHERE job_id=$id AND generation=$generation AND report IS NULL LIMIT 1;
            """, ("$id", id), ("$generation", fence.Generation));
        if (pending.ExecuteScalar() is not null) return 0;
        var cycle = used + 1;
        Execute(db, tx, "INSERT INTO review_cycles(job_id,cycle,generation) VALUES($id,$cycle,$generation);",
            ("$id", id), ("$cycle", cycle), ("$generation", fence.Generation));
        tx.Commit();
        return cycle;
    }

    internal void FinishReviewCycle(Guid id, ExecutionFence fence, int cycle, QualityResult report)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var job = ReadJob(db, tx, id) ?? throw new InvalidOperationException("Job missing.");
        JobRecovery.Require(job, fence);
        using var update = Command(db, tx, """
            UPDATE review_cycles SET head_sha=$head,report=$report
            WHERE job_id=$id AND generation=$generation AND cycle=$cycle AND report IS NULL;
            """, ("$id", id), ("$generation", fence.Generation), ("$cycle", cycle),
            ("$head", report.HeadSha), ("$report", Json.Stringify(report)));
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Review cycle is not open.");
        tx.Commit();
    }
}
