using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Factory;

public sealed record ReviewFinding(string Role, string Message);
public sealed record ReviewRecord(string Role, string Commit, string PolicyRevision, string Adapter,
    string Session, string Scope, string Verdict, string Assurance);
public sealed record ReviewApproval(Guid ProjectId, Guid JobId, long Generation, int Cycle, string Commit,
    string PolicyRevision, string Reviewer, string ReviewerSession, string Builder, string BuilderSession,
    DateTimeOffset ExpiresAt, string Verdict, string ProjectContractRevision);
public sealed record SignedReviewApproval(ReviewApproval Approval, string Signature);

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
            CREATE TABLE IF NOT EXISTS trusted_review_approvals(
                job_id TEXT NOT NULL, cycle INTEGER NOT NULL, payload TEXT NOT NULL, signature TEXT NOT NULL,
                PRIMARY KEY(job_id,cycle), FOREIGN KEY(job_id,cycle) REFERENCES review_cycles(job_id,cycle)
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

    internal void RecordTrustedReview(SignedReviewApproval input)
    {
        var key = Environment.GetEnvironmentVariable("FACTORY_REVIEW_PUBLIC_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No trusted review signer is configured.");
        var proof = input.Approval ?? throw new ArgumentException("Missing approval.");
        var now = DateTimeOffset.UtcNow;
        if (proof.PolicyRevision != ReviewPolicy.Revision || proof.Verdict != "PASS" ||
            !IntegrationVerifier.IsCommitSha(proof.Commit) || proof.ExpiresAt <= now || proof.ExpiresAt > now.AddHours(24) ||
            string.IsNullOrWhiteSpace(proof.Reviewer) || string.IsNullOrWhiteSpace(proof.Builder) ||
            string.Equals(proof.Reviewer, proof.Builder, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(proof.ReviewerSession) || string.IsNullOrWhiteSpace(proof.BuilderSession) ||
            string.Equals(proof.ReviewerSession, proof.BuilderSession, StringComparison.Ordinal))
            throw new ArgumentException("Review identity, verdict, scope or expiry is invalid.");
        var payload = Json.Stringify(proof);
        using var signer = RSA.Create();
        signer.ImportFromPem(key);
        if (!signer.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(input.Signature),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new ArgumentException("Invalid trusted review signature.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        var job = ReadJob(db, tx, proof.JobId);
        var project = Load(db, tx).Projects.SingleOrDefault(p => p.Id == proof.ProjectId);
        if (job is null || job.ProjectId != proof.ProjectId || job.ExecutionGeneration != proof.Generation ||
            job.State != JobStates.CompletedLocal || job.LeaseOwner.Length != 0 ||
            project is null || SpecificationContracts.ProjectRevision(project) != proof.ProjectContractRevision ||
            job.HeadSha != proof.Commit || job.SessionId != proof.BuilderSession ||
            string.IsNullOrEmpty(job.Worktree) || GitOperations.Head(job.Worktree) != proof.Commit)
            throw new InvalidOperationException("The approval does not match a current completed build.");
        using var query = Command(db, tx, """
            SELECT c.report,p.worker FROM review_cycles c JOIN execution_provenance p
              ON p.job_id=c.job_id AND p.generation=c.generation
            WHERE c.job_id=$job AND c.cycle=$cycle AND c.generation=$generation AND c.head_sha=$head
              AND c.cycle=(SELECT MAX(cycle) FROM review_cycles WHERE job_id=$job);
            """, ("$job", job.Id), ("$cycle", proof.Cycle), ("$generation", proof.Generation), ("$head", proof.Commit));
        using (var row = query.ExecuteReader())
        {
            if (!row.Read() || row.IsDBNull(0) || row.GetString(1) != proof.Builder)
                throw new InvalidOperationException("The approval does not match the builder provenance.");
            var report = JsonSerializer.Deserialize<QualityResult>(row.GetString(0), Json.Options);
            if (report is null || !report.Passed || report.HeadSha != proof.Commit || report.Findings.Count != 0 ||
                project.Checks.Count == 0 || report.Checks.Any(c => c.ExitCode != 0) ||
                !report.Checks.Select(c => c.Name).SequenceEqual(project.Checks.Select(c => c.Name)))
                throw new InvalidOperationException("Current deterministic quality evidence is required.");
        }
        Execute(db, tx, """
            INSERT INTO trusted_review_approvals(job_id,cycle,payload,signature) VALUES($job,$cycle,$payload,$signature)
            ON CONFLICT(job_id,cycle) DO UPDATE SET payload=excluded.payload,signature=excluded.signature;
            """, ("$job", job.Id), ("$cycle", proof.Cycle), ("$payload", payload), ("$signature", input.Signature));
        tx.Commit();
    }

    internal static void FinishRemoteReviewCycle(SqliteConnection db, SqliteTransaction tx, FactoryJob job, WorkerStatus request)
    {
        var result = request.Job;
        var head = result is not null && result.Id == job.Id && result.ProjectId == job.ProjectId &&
            result.Attempt == job.Attempt && IntegrationVerifier.IsCommitSha(result.HeadSha) ? result.HeadSha : null;
        var report = Json.Stringify(new { assurance = "UNKNOWN", head, outcome = job.State,
            findings = new[] { job.Error }, workerReport = request.Evidence is { Length: <= 1_000_000 } ? request.Evidence : null });
        Execute(db, tx, """
            UPDATE review_cycles SET head_sha=$head, report=$report
            WHERE job_id=$job AND generation=$generation AND report IS NULL;
            """, ("$job", job.Id), ("$generation", job.ExecutionGeneration), ("$head", head), ("$report", report));
    }

    internal bool IsReviewApproved(Guid id)
    {
        var key = Environment.GetEnvironmentVariable("FACTORY_REVIEW_PUBLIC_KEY");
        if (string.IsNullOrWhiteSpace(key)) return false;
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var job = ReadJob(db, tx, id);
        if (job is null || job.State != JobStates.CompletedLocal || job.LeaseOwner.Length != 0 ||
            string.IsNullOrEmpty(job.Worktree) ||
            !string.Equals(GitOperations.Head(job.Worktree), job.HeadSha, StringComparison.Ordinal))
            return false;
        using var query = Command(db, tx, """
            SELECT a.payload,a.signature FROM trusted_review_approvals a JOIN review_cycles c
                ON c.job_id=a.job_id AND c.cycle=a.cycle
            WHERE a.job_id=$job AND c.generation=$generation AND c.head_sha=$head
                AND c.cycle=(SELECT MAX(cycle) FROM review_cycles WHERE job_id=$job);
            """, ("$job", id), ("$generation", job.ExecutionGeneration), ("$head", job.HeadSha));
        using var row = query.ExecuteReader();
        if (!row.Read()) return false;
        var payload = row.GetString(0);
        var signature = row.GetString(1);
        row.Close();
        var proof = JsonSerializer.Deserialize<ReviewApproval>(payload, Json.Options);
        if (proof is null || proof.ExpiresAt <= DateTimeOffset.UtcNow || proof.PolicyRevision != ReviewPolicy.Revision ||
            proof.ProjectId != job.ProjectId || proof.JobId != id || proof.Generation != job.ExecutionGeneration ||
            proof.Commit != job.HeadSha || proof.BuilderSession != job.SessionId ||
            SpecificationContracts.ProjectRevision(Load(db, tx).Projects.Single(p => p.Id == job.ProjectId)) != proof.ProjectContractRevision)
            return false;
        using var signer = RSA.Create();
        signer.ImportFromPem(key);
        return signer.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signature),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }
}
