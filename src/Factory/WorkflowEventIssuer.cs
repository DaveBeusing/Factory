using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Factory;

internal static class WorkflowEventIssuer
{
    internal static WorkflowEventProof Issue(byte[] payload, string signature, string secret, RSA signer)
    {
        if (payload.Length is 0 or > 1_000_000 || string.IsNullOrWhiteSpace(secret) ||
            !signature.StartsWith("sha256=", StringComparison.Ordinal) || signature.Length != 71)
            throw new ArgumentException("Invalid GitHub webhook authentication.");
        byte[] supplied;
        try { supplied = Convert.FromHexString(signature[7..]); }
        catch (FormatException) { throw new ArgumentException("Invalid GitHub webhook authentication."); }
        if (!CryptographicOperations.FixedTimeEquals(supplied, HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)))
            throw new ArgumentException("Invalid GitHub webhook authentication.");

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        var run = root.GetProperty("workflow_run");
        if (root.GetProperty("action").GetString() != "completed" ||
            run.GetProperty("event").GetString() != "pull_request" ||
            run.GetProperty("conclusion").GetString() != "success")
            throw new InvalidOperationException("A successful GitHub pull request workflow event is required.");
        var links = run.GetProperty("pull_requests");
        if (links.GetArrayLength() != 1)
            throw new InvalidOperationException("The authenticated event must identify one exact pull request.");
        var pr = links[0];
        var head = pr.GetProperty("head");
        var basis = pr.GetProperty("base");
        var repository = root.GetProperty("repository");
        var repoId = repository.GetProperty("id").GetInt64();
        var headSha = head.GetProperty("sha").GetString() ?? "";
        var baseSha = basis.GetProperty("sha").GetString() ?? "";
        if (basis.GetProperty("repo").GetProperty("id").GetInt64() != repoId ||
            head.GetProperty("repo").GetProperty("id").GetInt64() != repoId ||
            run.GetProperty("head_sha").GetString() != headSha ||
            !IntegrationVerifier.IsCommitSha(headSha) || !IntegrationVerifier.IsCommitSha(baseSha))
            throw new InvalidOperationException("The workflow event does not identify matching repository commits.");
        var evidence = new WorkflowEvent(repository.GetProperty("full_name").GetString()!,
            run.GetProperty("workflow_id").GetInt64(), run.GetProperty("id").GetInt64(),
            run.GetProperty("run_attempt").GetInt32(), run.GetProperty("check_suite_id").GetInt64(),
            pr.GetProperty("number").GetInt32(), basis.GetProperty("ref").GetString()!, baseSha, headSha);
        return new WorkflowEventProof(evidence, Convert.ToBase64String(signer.SignData(
            Encoding.UTF8.GetBytes(Json.Stringify(evidence)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    }
}

public sealed partial class StateStore
{
    private static void UpgradeWorkflowDeliveries(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS workflow_deliveries (
                project_id TEXT NOT NULL,
                repository TEXT NOT NULL,
                workflow_id INTEGER NOT NULL,
                run_id INTEGER NOT NULL,
                attempt INTEGER NOT NULL,
                suite_id INTEGER NOT NULL,
                pull_number INTEGER NOT NULL,
                base_branch TEXT NOT NULL,
                base_commit TEXT NOT NULL,
                head_commit TEXT NOT NULL,
                at TEXT NOT NULL,
                payload TEXT NOT NULL,
                PRIMARY KEY(repository, workflow_id, run_id, attempt, suite_id, pull_number,
                    base_branch, base_commit, head_commit, project_id)
            ) WITHOUT ROWID;
            INSERT OR IGNORE INTO workflow_deliveries
            SELECT project_id, json_extract(payload,'$.event.repository'), json_extract(payload,'$.event.workflowId'),
                json_extract(payload,'$.event.runId'), json_extract(payload,'$.event.attempt'),
                json_extract(payload,'$.event.suiteId'), json_extract(payload,'$.event.pullNumber'),
                json_extract(payload,'$.event.baseBranch'), json_extract(payload,'$.event.baseCommit'),
                json_extract(payload,'$.event.headCommit'), at, payload
            FROM (SELECT project_id, at, rowid, CASE WHEN json_valid(reference) THEN reference END AS payload
                  FROM lifecycle_audit WHERE entity_kind='workflow-delivery')
            WHERE json_type(payload,'$.signature')='text'
                AND json_type(payload,'$.event.repository')='text'
                AND json_type(payload,'$.event.workflowId')='integer'
                AND json_type(payload,'$.event.runId')='integer'
                AND json_type(payload,'$.event.attempt')='integer'
                AND json_type(payload,'$.event.suiteId')='integer'
                AND json_type(payload,'$.event.pullNumber')='integer'
                AND json_type(payload,'$.event.baseBranch')='text'
                AND json_type(payload,'$.event.baseCommit')='text'
                AND json_type(payload,'$.event.headCommit')='text'
            ORDER BY at DESC, rowid DESC;
            CREATE INDEX IF NOT EXISTS projects_workflow_base ON projects(base_branch);
            PRAGMA user_version=26;
            """);

    private static (string, object?)[] WorkflowIdentity(WorkflowEvent evidence) =>
    [
        ("$repository", evidence.Repository), ("$workflow", evidence.WorkflowId), ("$run", evidence.RunId),
        ("$attempt", evidence.Attempt), ("$suite", evidence.SuiteId), ("$pull", evidence.PullNumber),
        ("$branch", evidence.BaseBranch), ("$base", evidence.BaseCommit), ("$head", evidence.HeadCommit)
    ];

    internal void RecordWorkflowDelivery(WorkflowEventProof proof)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var projects = new List<Guid>();
        using (var lookup = Command(db, tx, "SELECT id,origin FROM projects WHERE base_branch=$branch;",
            ("$branch", proof.Event.BaseBranch)))
        using (var rows = lookup.ExecuteReader())
            while (rows.Read())
                if (MatchesWorkflowRepository(rows.GetString(1), proof.Event.Repository))
                    projects.Add(Guid.Parse(rows.GetString(0)));
        if (projects.Count == 0)
            throw new InvalidOperationException("The delivery does not match a registered repository and base.");
        var payload = Json.Stringify(proof);
        var identity = WorkflowIdentity(proof.Event);
        var at = DateTimeOffset.UtcNow;
        foreach (var projectId in projects)
        {
            using var insert = Command(db, tx, """
                INSERT INTO workflow_deliveries(project_id,repository,workflow_id,run_id,attempt,suite_id,
                    pull_number,base_branch,base_commit,head_commit,at,payload)
                VALUES($project,$repository,$workflow,$run,$attempt,$suite,$pull,$branch,$base,$head,$at,$payload)
                ON CONFLICT(repository,workflow_id,run_id,attempt,suite_id,pull_number,
                    base_branch,base_commit,head_commit,project_id) DO NOTHING;
                """, [.. identity, ("$project", projectId), ("$at", at), ("$payload", payload)]);
            if (insert.ExecuteNonQuery() == 1)
                RecordLifecycle(db, tx, projectId, "workflow-delivery", projectId, "github-webhook", "received",
                    "authenticated", LifecycleReason.ApprovalGranted, reference: payload);
        }
        tx.Commit();
    }

    internal WorkflowEventProof? FindWorkflowDelivery(WorkflowEvent expected)
    {
        using var db = Open();
        using var query = Command(db, null, """
            SELECT payload FROM workflow_deliveries
            WHERE repository=$repository AND workflow_id=$workflow AND run_id=$run AND attempt=$attempt
                AND suite_id=$suite AND pull_number=$pull AND base_branch=$branch AND base_commit=$base AND head_commit=$head
            ORDER BY at DESC;
            """, WorkflowIdentity(expected));
        using var rows = query.ExecuteReader();
        while (rows.Read())
        {
            try
            {
                var proof = JsonSerializer.Deserialize<WorkflowEventProof>(rows.GetString(0), Json.Options);
                if (proof?.Event == expected) return proof;
            }
            catch (JsonException)
            {
                // Malformed historical rows remain available in the durable audit.
            }
        }
        return null;
    }

    private static bool MatchesWorkflowRepository(string origin, string repository)
    {
        try { return string.Equals(GitHubWorkerResultVerifier.GitHubRepository(origin), repository, StringComparison.OrdinalIgnoreCase); }
        catch (InvalidOperationException) { return false; }
    }
}
