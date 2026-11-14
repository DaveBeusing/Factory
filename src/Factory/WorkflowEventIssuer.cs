using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    internal void RecordWorkflowDelivery(WorkflowEventProof proof)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var projects = Load(db, tx).Projects.Where(project =>
            string.Equals(project.BaseBranch, proof.Event.BaseBranch, StringComparison.Ordinal) &&
            MatchesWorkflowRepository(project.Origin, proof.Event.Repository)).ToArray();
        if (projects.Length == 0)
            throw new InvalidOperationException("The delivery does not match a registered repository and base.");
        var payload = Json.Stringify(proof);
        foreach (var project in projects)
        {
            using var query = Command(db, tx, """
                SELECT reference FROM lifecycle_audit WHERE project_id=$project AND entity_kind='workflow-delivery';
                """, ("$project", project.Id));
            using var rows = query.ExecuteReader();
            var duplicate = false;
            while (rows.Read())
            {
                var existing = JsonSerializer.Deserialize<WorkflowEventProof>(rows.GetString(0), Json.Options);
                if (existing?.Event == proof.Event) { duplicate = true; break; }
            }
            rows.Close();
            if (!duplicate)
                RecordLifecycle(db, tx, project.Id, "workflow-delivery", project.Id, "github-webhook", "received",
                    "authenticated", LifecycleReason.ApprovalGranted, reference: payload);
        }
        tx.Commit();
    }

    internal WorkflowEventProof? FindWorkflowDelivery(WorkflowEvent expected)
    {
        using var db = Open();
        using var query = Command(db, null, """
            SELECT reference FROM lifecycle_audit WHERE entity_kind='workflow-delivery' ORDER BY at DESC;
            """);
        using var rows = query.ExecuteReader();
        while (rows.Read())
        {
            var proof = JsonSerializer.Deserialize<WorkflowEventProof>(rows.GetString(0), Json.Options);
            if (proof?.Event == expected) return proof;
        }
        return null;
    }

    private static bool MatchesWorkflowRepository(string origin, string repository)
    {
        try { return string.Equals(GitHubWorkerResultVerifier.GitHubRepository(origin), repository, StringComparison.OrdinalIgnoreCase); }
        catch (InvalidOperationException) { return false; }
    }
}
