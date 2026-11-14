using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory;

// A successful worker response is a claim, not an authorization to mark a job PR-ready.
public interface IWorkerResultVerifier
{
    Task<VerifiedWorkerResult> VerifyAsync(ProjectInfo project, FactoryJob job,
        WorkerStatus status, CancellationToken ct);
}

public sealed record VerifiedWorkerResult(string EvidenceJson, string? TreeSha = null);

public sealed class GitHubWorkerResultVerifier : IWorkerResultVerifier
{
    private static readonly string[] RequiredReviewRoles = ["code", "architecture", "security", "ux"];
    private readonly Func<string[], int, CancellationToken, Task<CommandResult>> _runGitHub;

    public GitHubWorkerResultVerifier() : this((args, timeout, ct) =>
        Commands.RunAsync("gh", args, Environment.CurrentDirectory, timeout, cancellationToken: ct)) { }

    internal GitHubWorkerResultVerifier(Func<string[], int, CancellationToken, Task<CommandResult>> runGitHub) =>
        _runGitHub = runGitHub;

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
        WorkPackageScope.ValidateDeclaration(job.ExpectedPaths);
        // A worker report cannot establish controller-trusted reviewer independence.
        var evidence = ValidateEvidence(project, job, status);
        RequireTrustedReviewProvenance(evidence);
        using var pr = await PullAsync(repository, number, ct);
        ValidatePull(pr, repository, project.BaseBranch, expectedBranch, finished.HeadSha);

        // A passing worker-side check cannot substitute for required, independent GitHub checks.
        var baseSha = ReadBaseSha(pr);
        var checks = await RequiredChecksAsync(repository, number, project.BaseBranch, finished.HeadSha, ct);
        foreach (var check in project.Checks)
        {
            if (!checks.Contains(check.Name))
                throw new InvalidOperationException("Project check is not a successful required GitHub check: " + check.Name);
        }

        // Controller-side enforcement uses GitHub PR files, not worker-reported paths.
        var changed = await CompareFilePathsAsync(repository, baseSha, finished.HeadSha, ct);
        WorkPackageScope.Enforce(job.ExpectedPaths, changed);
        GitOperations.EnforcePaths(changed, project.ProtectedPaths);

        string? tree = null;
        if (status.ProviderLeaseId is not null)
        {
            var response = await _runGitHub(["api", $"repos/{repository}/git/commits/{finished.HeadSha}"], 45, ct);
            if (!response.Ok) throw new InvalidOperationException("Cannot independently read the verified commit tree");
            using var commit = JsonDocument.Parse(response.Output);
            if (commit.RootElement.GetProperty("sha").GetString() != finished.HeadSha)
                throw new InvalidOperationException("GitHub returned a different commit");
            tree = commit.RootElement.GetProperty("tree").GetProperty("sha").GetString();
            if (tree is null || !IntegrationVerifier.IsCommitSha(tree))
                throw new InvalidOperationException("GitHub did not return a valid commit tree");
        }

        // Recheck the PR after all other GitHub requests, including the commit tree lookup.
        using var latest = await PullAsync(repository, number, ct);
        ValidatePull(latest, repository, project.BaseBranch, expectedBranch, finished.HeadSha);
        if (ReadBaseSha(latest) != baseSha)
            throw new InvalidOperationException("GitHub PR base commit changed during verification");
        return new VerifiedWorkerResult(Json.Stringify(new
        {
            verifiedBy = "controller/github-required-checks",
            repository,
            prNumber = number,
            branch = expectedBranch,
            baseCommit = baseSha,
            commit = finished.HeadSha,
            verifiedAt = DateTimeOffset.UtcNow,
            requiredChecks = checks.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            verifiedChangedPaths = changed,
            verifiedTree = tree,
            // Worker data remains explicitly identified as worker-reported evidence.
            workerEvidence = evidence
        }), tree);
    }

    private static void RequireTrustedReviewProvenance(JsonElement workerEvidence)
    {
        // The current remote protocol does not carry controller-issued reviewer decisions.
        // A worker-defined assurance label must never authorize PR-ready.
        throw new InvalidOperationException("Controller-trusted independent reviewer provenance is unavailable.");
    }

    public static JsonElement ValidateEvidence(ProjectInfo project, FactoryJob job, WorkerStatus status)
    {
        if (project.Checks.Count == 0)
            throw new InvalidOperationException("No quality checks are configured");
        if (string.IsNullOrWhiteSpace(status.Evidence) || status.Evidence.Length > 1_000_000)
            throw new InvalidOperationException("Worker quality evidence is missing or oversized");
        var reported = status.Job ?? throw new InvalidOperationException("Missing worker completion");
        using var document = JsonDocument.Parse(status.Evidence);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("jobId").GetGuid() != job.Id ||
            root.GetProperty("attempt").GetInt32() != job.Attempt ||
            root.GetProperty("outcome").GetString() != "ready" ||
            !string.Equals(root.GetProperty("commit").GetString(),
                reported.HeadSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Worker evidence identity is inconsistent");

        var report = root.GetProperty("report");
        if (!report.GetProperty("passed").GetBoolean() ||
            !string.Equals(report.GetProperty("headSha").GetString(),
                reported.HeadSha, StringComparison.OrdinalIgnoreCase) ||
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

    private async Task<JsonDocument> PullAsync(string repository, int number, CancellationToken ct)
    {
        var response = await _runGitHub(["api", $"repos/{repository}/pulls/{number}"], 45, ct);
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

    public static IReadOnlyList<string> ParsePullFilePaths(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Incomplete GitHub PR file listing");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileCount = 0;
        foreach (var page in document.RootElement.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Incomplete GitHub PR file page");
            foreach (var file in page.EnumerateArray())
            {
                fileCount++;
                if (fileCount >= 3000)
                    throw new InvalidOperationException("GitHub PR file limit reached; scope cannot be verified completely");
                var changed = file.GetProperty("filename").GetString();
                if (string.IsNullOrWhiteSpace(changed))
                    throw new InvalidOperationException("GitHub PR file path missing");
                paths.Add(changed);
                if (file.TryGetProperty("previous_filename", out var previous) &&
                    previous.ValueKind == JsonValueKind.String)
                {
                    var prior = previous.GetString();
                    if (!string.IsNullOrWhiteSpace(prior)) paths.Add(prior);
                }
            }
        }
        if (fileCount == 0)
            throw new InvalidOperationException("No PR file changes could be verified");
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ReadBaseSha(JsonDocument pull)
    {
        var sha = pull.RootElement.GetProperty("base").GetProperty("sha").GetString();
        return sha is not null && IntegrationVerifier.IsCommitSha(sha)
            ? sha : throw new InvalidOperationException("GitHub PR base commit is missing or invalid");
    }

    private async Task<IReadOnlyList<string>> CompareFilePathsAsync(
        string repository, string baseSha, string headSha, CancellationToken ct)
    {
        var response = await _runGitHub(
            ["api", $"repos/{repository}/compare/{baseSha}...{headSha}?per_page=1"], 60, ct);
        if (!response.Ok)
            throw new InvalidOperationException("Unable to verify changed files for the claimed commits");
        using var document = JsonDocument.Parse(response.Output);
        if (document.RootElement.GetProperty("base_commit").GetProperty("sha").GetString() != baseSha)
            throw new InvalidOperationException("GitHub comparison returned a different base commit");
        var files = document.RootElement.GetProperty("files");
        // GitHub only includes the comparison file list on the first page and caps it at 300.
        if (files.GetArrayLength() >= 300)
            throw new InvalidOperationException("GitHub comparison file limit reached");
        foreach (var file in files.EnumerateArray())
            if (file.GetProperty("status").GetString() == "renamed" &&
                (!file.TryGetProperty("previous_filename", out var previous) || string.IsNullOrWhiteSpace(previous.GetString())))
                throw new InvalidOperationException("GitHub renamed file source is missing");
        return ParsePullFilePaths("[" + files.GetRawText() + "]");
    }

    private async Task<HashSet<string>> RequiredChecksAsync(string repository, int number, string baseBranch,
        string sha, CancellationToken ct)
    {
        const string query = """
            query($owner: String!, $name: String!, $base: String!, $sha: GitObjectID!, $pr: Int!) {
              repository(owner: $owner, name: $name) {
                ref(qualifiedName: $base) {
                  refUpdateRule { requiredStatusCheckContexts }
                  branchProtectionRule {
                    requiresStatusChecks requiredStatusCheckContexts requiredStatusChecks { context app { databaseId } }
                  }
                }
                object(oid: $sha) { ... on Commit { oid statusCheckRollup {
                  contexts(first: 100) { pageInfo { hasNextPage } nodes {
                    ... on CheckRun { name isRequired(pullRequestNumber: $pr) conclusion checkSuite { app { databaseId } } }
                    ... on StatusContext { context isRequired(pullRequestNumber: $pr) state }
                  } }
                } } }
              }
            }
            """;
        var names = repository.Split('/');
        var result = await _runGitHub(
            ["api", "graphql", "-f", "query=" + query, "-f", "owner=" + names[0],
             "-f", "name=" + names[1], "-f", "base=refs/heads/" + baseBranch,
             "-f", "sha=" + sha, "-F", $"pr={number}"], 60, ct);
        if (!result.Ok) throw new InvalidOperationException("Required GitHub checks are not successful");
        using var document = JsonDocument.Parse(result.Output);
        var root = document.RootElement;
        if (root.TryGetProperty("errors", out _))
            throw new InvalidOperationException("GitHub returned incomplete check evidence");
        var repo = root.GetProperty("data").GetProperty("repository");
        var commit = repo.GetProperty("object");
        if (commit.GetProperty("oid").GetString() != sha)
            throw new InvalidOperationException("GitHub checks belong to a different commit");
        var contexts = commit.GetProperty("statusCheckRollup").GetProperty("contexts");
        if (contexts.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())
            throw new InvalidOperationException("GitHub check listing exceeds the verification limit");
        var checks = contexts.GetProperty("nodes").EnumerateArray()
            .Where(c => c.GetProperty("isRequired").GetBoolean()).ToArray();
        if (checks.Length == 0 || checks.Any(c =>
            (c.TryGetProperty("conclusion", out var value) ? value : c.GetProperty("state")).GetString() != "SUCCESS"))
            throw new InvalidOperationException("Required GitHub checks are missing, pending or failing");
        var successful = checks.Select(c => (c.TryGetProperty("name", out var name) ? name : c.GetProperty("context")).GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        // The rollup only contains checks that exist. Read the branch policy independently.
        VerifyClassicProtection(repo.GetProperty("ref"), successful, checks);

        var rulesResult = await _runGitHub(
            ["api", "--paginate", "--slurp", $"repos/{repository}/rules/branches/{Uri.EscapeDataString(baseBranch)}?per_page=100"], 45, ct);
        if (!rulesResult.Ok) throw new InvalidOperationException("Cannot verify required GitHub branch rules");
        using var rules = JsonDocument.Parse(rulesResult.Output);
        if (rules.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException("GitHub returned no branch rules page");
        foreach (var rule in rules.RootElement.EnumerateArray().SelectMany(page => page.EnumerateArray()))
        {
            var type = rule.GetProperty("type").GetString();
            if (type == "workflows")
                throw new InvalidOperationException("Required GitHub workflow rules cannot be verified from check contexts");
            if (type != "required_status_checks") continue;
            foreach (var check in rule.GetProperty("parameters").GetProperty("required_status_checks").EnumerateArray())
            {
                var context = check.GetProperty("context");
                RequireSuccessfulContext(context, successful);
                if (check.TryGetProperty("integration_id", out var appId) && appId.ValueKind != JsonValueKind.Null)
                    RequireSuccessfulApp(context, appId, checks);
            }
        }
        return successful;
    }

    private static void VerifyClassicProtection(JsonElement branch, HashSet<string> successful, JsonElement[] checks)
    {
        const string unavailable = "Cannot verify classic GitHub required check sources; use a controller credential that can read branch protection";
        var visibleRule = branch.GetProperty("refUpdateRule");
        var visibleContexts = visibleRule.ValueKind == JsonValueKind.Null
            ? default : visibleRule.GetProperty("requiredStatusCheckContexts");
        var protection = branch.GetProperty("branchProtectionRule");
        if (protection.ValueKind == JsonValueKind.Null || !protection.GetProperty("requiresStatusChecks").GetBoolean())
        {
            // Read-only viewers can see the context names even when GitHub hides the rule and app pins.
            if (visibleContexts.ValueKind == JsonValueKind.Array && visibleContexts.GetArrayLength() > 0)
                throw new InvalidOperationException(unavailable);
            return;
        }

        var required = protection.GetProperty("requiredStatusChecks");
        if (required.ValueKind != JsonValueKind.Array) throw new InvalidOperationException(unavailable);
        var described = new HashSet<string?>(StringComparer.Ordinal);
        foreach (var check in required.EnumerateArray())
        {
            var context = check.GetProperty("context");
            RequireSuccessfulContext(context, successful);
            described.Add(context.GetString());
            var app = check.GetProperty("app");
            if (app.ValueKind != JsonValueKind.Null)
                RequireSuccessfulApp(context, app.GetProperty("databaseId"), checks);
        }
        if (protection.GetProperty("requiredStatusCheckContexts").EnumerateArray().Any(c => !described.Contains(c.GetString())) ||
            (visibleContexts.ValueKind == JsonValueKind.Array && visibleContexts.EnumerateArray().Any(c => !described.Contains(c.GetString()))))
            throw new InvalidOperationException(unavailable);
    }

    private static void RequireSuccessfulContext(JsonElement context, HashSet<string> successful)
    {
        var name = context.GetString();
        if (string.IsNullOrWhiteSpace(name) || !successful.Contains(name))
            throw new InvalidOperationException("Required GitHub check has no successful result at the verified commit: " + name);
    }

    private static void RequireSuccessfulApp(JsonElement context, JsonElement appId, JsonElement[] checks)
    {
        var name = context.GetString();
        if (appId.ValueKind != JsonValueKind.Number || !appId.TryGetInt64(out var expected) || expected <= 0 ||
            !checks.Any(check => check.TryGetProperty("name", out var checkName) && checkName.GetString() == name &&
                check.TryGetProperty("checkSuite", out var suite) && suite.ValueKind == JsonValueKind.Object &&
                suite.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object &&
                app.TryGetProperty("databaseId", out var actual) && actual.ValueKind == JsonValueKind.Number &&
                actual.TryGetInt64(out var id) && id == expected))
            throw new InvalidOperationException("Required GitHub check has no successful result from its configured app: " + name);
    }
}
