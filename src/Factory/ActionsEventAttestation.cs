using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory;

internal sealed class ActionsEventAttestation(Func<string[], int, CancellationToken, Task<CommandResult>> github)
{
    internal async Task VerifyAsync(WorkflowEvent expected, string workflowFile, CancellationToken ct)
    {
        if (!Regex.IsMatch(workflowFile, @"\A[A-Za-z0-9_.-]+\.ya?ml\z"))
            throw new InvalidOperationException("Invalid attestation workflow path.");
        var path = $".github/workflows/{workflowFile}";
        using var workflow = JsonDocument.Parse(Commands.Require(await github(
            ["api", $"repos/{expected.Repository}/actions/workflows/{expected.WorkflowId}"], 45, ct), "read attestation workflow"));
        if (workflow.RootElement.GetProperty("path").GetString() != path)
            throw new InvalidOperationException("Attestation workflow ID and path differ.");

        var baseBlob = await ReadBlobAsync(expected.Repository, path, expected.BaseCommit, ct);
        var headBlob = await ReadBlobAsync(expected.Repository, path, expected.HeadCommit, ct);
        if (baseBlob != headBlob)
            throw new InvalidOperationException("Attestation requires the reviewed workflow to match the signed base workflow.");
        var receipt = new { expected.Repository, expected.RunId, expected.Attempt, expected.PullNumber,
            expected.BaseBranch, expected.BaseCommit, expected.HeadCommit };
        var directory = Directory.CreateTempSubdirectory("factory-attestation-");
        var file = Path.Combine(directory.FullName, "pr-event.json");
        try
        {
            var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions(Json.Options) { WriteIndented = false });
            await File.WriteAllTextAsync(file, json, new UTF8Encoding(false), ct);
            var result = await github(["attestation", "verify", file, "--repo", expected.Repository,
                "--signer-workflow", $"{expected.Repository}/{path}", "--deny-self-hosted-runners",
                "--format", "json"], 60, ct);
            using var evidence = JsonDocument.Parse(Commands.Require(result, "verify GitHub Actions event attestation"));
            var invocation = $"https://github.com/{expected.Repository}/actions/runs/{expected.RunId}/attempts/{expected.Attempt}";
            foreach (var item in evidence.RootElement.EnumerateArray())
            {
                var certificate = item.GetProperty("verificationResult").GetProperty("signature").GetProperty("certificate");
                if (certificate.GetProperty("runInvocationURI").GetString() != invocation ||
                    certificate.GetProperty("sourceRepositoryRef").GetString() != $"refs/pull/{expected.PullNumber}/merge")
                    continue;
                var digest = certificate.GetProperty("buildSignerDigest").GetString() ?? "";
                if (!IntegrationVerifier.IsCommitSha(digest) ||
                    certificate.GetProperty("sourceRepositoryDigest").GetString() != digest) continue;
                using var commit = JsonDocument.Parse(Commands.Require(await github(
                    ["api", $"repos/{expected.Repository}/git/commits/{digest}"], 45, ct), "read attested merge commit"));
                if (commit.RootElement.GetProperty("sha").GetString() != digest ||
                    !commit.RootElement.GetProperty("parents").EnumerateArray().Select(parent =>
                        parent.GetProperty("sha").GetString()).SequenceEqual(new[] { expected.BaseCommit, expected.HeadCommit }))
                    continue;
                if (await ReadBlobAsync(expected.Repository, path, digest, ct) == baseBlob) return;
            }
            throw new InvalidOperationException("The attestation does not prove the authorized workflow attempt and PR merge parents.");
        }
        finally
        {
            File.Delete(file);
            directory.Delete();
        }
    }

    private async Task<string> ReadBlobAsync(string repository, string path, string commit, CancellationToken ct)
    {
        using var contents = JsonDocument.Parse(Commands.Require(await github(
            ["api", $"repos/{repository}/contents/{path}?ref={commit}"], 45, ct), "read reviewed workflow"));
        var sha = contents.RootElement.GetProperty("sha").GetString() ?? "";
        return IntegrationVerifier.IsCommitSha(sha) ? sha :
            throw new InvalidOperationException("GitHub did not return a valid workflow blob ID.");
    }
}
