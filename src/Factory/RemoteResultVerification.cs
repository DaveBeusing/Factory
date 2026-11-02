using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory;

// A successful worker response is a claim, not an authorization to mark a job PR-ready.
public interface IWorkerResultVerifier
{
    Task<VerifiedWorkerResult> VerifyAsync(ProjectInfo project, FactoryJob job,
        WorkerStatus status, CancellationToken ct);
}

public sealed record VerifiedWorkerResult(string EvidenceJson);

public sealed class GitHubWorkerResultVerifier : IWorkerResultVerifier
{
    private static readonly string[] RequiredReviewRoles = ["code", "architecture", "security", "ux"];

    public async Task<VerifiedWorkerResult> VerifyAsync(ProjectInfo project, FactoryJob job,
        WorkerStatus status, CancellationToken ct)
    {
        var finished = status.Job ?? throw new InvalidOperationException("Missing worker result");
        if (finished.State != JobStates.PrReady || finished.Id != job.Id ||
            finished.ProjectId != job.ProjectId || finished.Attempt != job.Attempt ||
            finished.Mode != JobExecutionModes.Remote)
            throw new InvalidOperationException("Worker result does not match its active claim");

        var expectedBranch = $"factory/{job.Id:N}";
        if (finished.Branch != expectedBranch ||
            !Regex.IsMatch(finished.HeadSha, "^[0-9a-fA-F]{40}$"))
            throw new InvalidOperationException("Worker branch or commit is invalid");

        var repository = GitHubRepository(project.Origin);
        var number = PullNumber(finished.PrUrl, repository);
        var evidence = ValidateEvidence(project, job, status);
        using var pr = await PullAsync(repository, number, ct);
        ValidatePull(pr, repository, project.BaseBranch, expectedBranch, finished.HeadSha);

        // A passing worker-side check cannot substitute for required, independent GitHub checks.
        var checks = await RequiredChecksAsync(repository, number, ct);
        foreach (var check in project.Checks)
        {
            if (!checks.Contains(check.Name))
                throw new InvalidOperationException("Project check is not a successful required GitHub check: " + check.Name);
        }

        // Prevent accepting a PR whose head moved while the checks were inspected.
        using var latest = await PullAsync(repository, number, ct);
        ValidatePull(latest, repository, project.BaseBranch, expectedBranch, finished.HeadSha);
        return new VerifiedWorkerResult(Json.Stringify(new
        {
            verifiedBy = "controller/github-required-checks",
            repository,
            prNumber = number,
            branch = expectedBranch,
            commit = finished.HeadSha,
            verifiedAt = DateTimeOffset.UtcNow,
            requiredChecks = checks.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            // Worker data remains explicitly identified as worker-reported evidence.
            workerEvidence = evidence
        }));
    }

    public static JsonElement ValidateEvidence(ProjectInfo project, FactoryJob job, WorkerStatus status)
    {
        if (project.Checks.Count == 0)
            throw new InvalidOperationException("No quality checks are configured");
        if (string.IsNullOrWhiteSpace(status.Evidence) || status.Evidence.Length > 1_000_000)
            throw new InvalidOperationException("Worker quality evidence is missing or oversized");
        using var document = JsonDocument.Parse(status.Evidence);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("jobId").GetGuid() != job.Id ||
            root.GetProperty("attempt").GetInt32() != job.Attempt ||
            root.GetProperty("outcome").GetString() != "ready" ||
            !string.Equals(root.GetProperty("commit").GetString(),
                status.Job?.HeadSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Worker evidence identity is inconsistent");

        var report = root.GetProperty("report");
        if (!report.GetProperty("passed").GetBoolean() ||
            !string.Equals(report.GetProperty("headSha").GetString(),
                status.Job.HeadSha, StringComparison.OrdinalIgnoreCase) ||
            report.GetProperty("findings").GetArrayLength() != 0)
            throw new InvalidOperationException("Worker quality report did not pass for this commit");

        var checks = report.GetProperty("checks").EnumerateArray().ToArray();
        var expected = project.Checks.Select(c => c.Name).ToArray();
        if (expected.Any(string.IsNullOrWhiteSpace) || expected.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
            checks.Length != expected.Length ||
            checks.Any(c => c.GetProperty("exitCode").GetInt32() != 0) ||
            !checks.Select(c => c.GetProperty("name").GetString())
                .OrderBy(n => n, StringComparer.Ordinal)
                .SequenceEqual(expected.OrderBy(n => n, StringComparer.Ordinal)))
            throw new InvalidOperationException("Worker quality checks do not match project requirements");

        var reviews = report.GetProperty("reviews");
        if (reviews.ValueKind != JsonValueKind.Object ||
            reviews.EnumerateObject().Count() != RequiredReviewRoles.Length ||
            RequiredReviewRoles.Any(role => !reviews.TryGetProperty(role, out var verdict) ||
                verdict.ValueKind != JsonValueKind.String ||
                !verdict.GetString()!.StartsWith("PASS\n", StringComparison.Ordinal)))
            throw new InvalidOperationException("Worker review evidence is incomplete");
        return root.Clone();
    }

    public static string GitHubRepository(string origin)
    {
        string path;
        if (origin.StartsWith("git@github.com:", StringComparison.Ordinal))
            path = origin["git@github.com:".Length..];
        else if (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            (uri.Scheme == "https" || (uri.Scheme == "ssh" && uri.UserInfo == "git")) &&
            uri.IsDefaultPort && uri.Query.Length == 0 && uri.Fragment.Length == 0)
            path = uri.AbsolutePath.Trim('/');
        else
            throw new InvalidOperationException("Remote result verification requires a trusted GitHub origin");
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (!Regex.IsMatch(path, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new InvalidOperationException("Invalid GitHub repository origin");
        return path;
    }

    public static int PullNumber(string url, string repository)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Untrusted pull request URL");
        var prefix = "/" + repository + "/pull/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(uri.AbsolutePath[prefix.Length..], out var number) || number <= 0)
            throw new InvalidOperationException("Pull request belongs to another repository");
        return number;
    }

    private static async Task<JsonDocument> PullAsync(string repository, int number, CancellationToken ct)
    {
        var response = await Commands.RunAsync("gh",
            ["api", $"repos/{repository}/pulls/{number}"], Environment.CurrentDirectory, 45,
            cancellationToken: ct);
        if (!response.Ok) throw new InvalidOperationException("Cannot independently read the GitHub pull request");
        return JsonDocument.Parse(response.Output);
    }

    public static void ValidatePull(JsonDocument document, string repository, string baseBranch,
        string expectedBranch, string expectedSha)
    {
        var pr = document.RootElement;
        if (pr.GetProperty("state").GetString() != "open" ||
            !string.Equals(pr.GetProperty("base").GetProperty("repo").GetProperty("full_name").GetString(),
                repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pr.GetProperty("head").GetProperty("repo").GetProperty("full_name").GetString(),
                repository, StringComparison.OrdinalIgnoreCase) ||
            pr.GetProperty("base").GetProperty("ref").GetString() != baseBranch ||
            pr.GetProperty("head").GetProperty("ref").GetString() != expectedBranch ||
            !string.Equals(pr.GetProperty("head").GetProperty("sha").GetString(),
                expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub PR repository, branch, base or head commit differs from the claim");
    }

    private static async Task<HashSet<string>> RequiredChecksAsync(string repository, int number, CancellationToken ct)
    {
        var result = await Commands.RunAsync("gh",
            ["pr", "checks", number.ToString(), "--repo", repository, "--required",
             "--json", "name,state"], Environment.CurrentDirectory, 60, cancellationToken: ct);
        if (!result.Ok) throw new InvalidOperationException("Required GitHub checks are not successful");
        using var document = JsonDocument.Parse(result.Output);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Cannot read required GitHub checks");
        var checks = document.RootElement.EnumerateArray().ToArray();
        if (checks.Length == 0 || checks.Any(c => c.GetProperty("state").GetString() != "SUCCESS"))
            throw new InvalidOperationException("Required GitHub checks are missing, pending or failing");
        return checks.Select(c => c.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
