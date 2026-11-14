using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Factory;

public sealed record PullRequestDecision(Guid ProjectId, Guid JobId, string ProjectRevision, string Repository,
    string BaseBranch, string BaseCommit, string HeadCommit, string Account, string Action, string Method,
    int PullNumber, DateTimeOffset ExpiresAt, string ScopeRevision, bool BranchChecksAvailable = true,
    string? WorkflowFile = null, long WorkflowId = 0);
public sealed record SignedPullRequestDecision(PullRequestDecision Decision, string Signature);

internal sealed class PullRequestCoordinator(StateStore store, FactorySettings settings,
    Func<string[], int, CancellationToken, Task<CommandResult>> github)
{
    internal PullRequestCoordinator(StateStore store, FactorySettings settings) : this(store, settings,
        (args, timeout, ct) => Commands.RunAsync("gh", args, Environment.CurrentDirectory, timeout, cancellationToken: ct)) { }

    internal async Task<object> ExecuteAsync(Guid id, SignedPullRequestDecision signed, CancellationToken ct)
    {
        var d = signed.Decision ?? throw new ArgumentException("Missing project decision.");
        if (d.Action is not ("draft" or "merge") || !settings.AllowPush ||
            (d.Action == "merge" && !settings.AutoMerge))
            throw new InvalidOperationException("Publication or guarded merge is disabled.");
        ValidateSignature(signed);
        var job = store.Job(id);
        var project = store.Read().Projects.Single(p => p.Id == job.ProjectId);
        var branch = $"factory/{id:N}";
        ValidateTarget(d, job, project);
        if (!store.IsReviewApproved(id))
            throw new InvalidOperationException("A current signed independent review is required.");

        var account = await ReadAsync(["api", "user"], ct);
        var repository = await ReadAsync(["api", $"repos/{d.Repository}"], ct);
        RequireHostPermission(d, account, repository);
        if (d.Action == "draft")
        {
            await RequireCurrentBaseAsync(d, ct);
            var existing = await ReadAsync(["api", $"repos/{d.Repository}/pulls?state=all&head={Uri.EscapeDataString(d.Repository.Split('/')[0] + ":" + branch)}"], ct);
            if (existing.GetArrayLength() != 0)
                throw new InvalidOperationException("An existing pull request requires reconciliation.");
            var verifierForDraft = new GitHubWorkerResultVerifier(github);
            var changes = await verifierForDraft.CompareFilePathsAsync(d.Repository, d.BaseCommit, d.HeadCommit, ct);
            WorkPackageScope.Enforce(job.ExpectedPaths, changes);
            GitOperations.EnforcePaths(changes, project.ProtectedPaths);
            if (d.BranchChecksAvailable)
                await RequireBranchChecksAsync(d, project, ct);
            else
                await RequirePullRequestOnlyAsync(d, project, ct);
            await RequireImmutableHeadAsync(d, branch, ct);
            Reserve(signed, job, project);
            await RequireCurrentBaseAsync(d, ct);
            var remoteHead = await ReadAsync(["api", $"repos/{d.Repository}/git/ref/heads/{branch}"], ct);
            if (remoteHead.GetProperty("object").GetProperty("sha").GetString() != d.HeadCommit)
                throw new InvalidOperationException("The pre-published branch does not contain the approved head.");
            await RequireImmutableHeadAsync(d, branch, ct);
            RequireCurrentDecision(signed, id);
            var created = await MutateAsync(["api", "--method", "POST", $"repos/{d.Repository}/pulls",
                "-f", "head=" + branch, "-f", "base=" + d.BaseBranch, "-f", "title=" + job.Title,
                "-F", "draft=true", "-f", "body=Required PR CI evaluation for Factory job " + id], ct);
            using var receipt = JsonDocument.Parse(created.GetRawText());
            GitHubWorkerResultVerifier.ValidatePull(receipt, d.Repository, d.BaseBranch, branch, d.HeadCommit);
            if (!created.GetProperty("draft").GetBoolean() ||
                created.GetProperty("base").GetProperty("sha").GetString() != d.BaseCommit)
                throw new InvalidOperationException("The created PR differs from the authorized draft; reconcile it in GitHub.");
            return new { url = created.GetProperty("html_url").GetString(), d.HeadCommit, draft = true };
        }

        var url = $"https://github.com/{d.Repository}/pull/{d.PullNumber}";
        var verifier = new GitHubWorkerResultVerifier(github);
        var pull = await ReadPullAsync(d, ct);
        if (pull.GetProperty("merged").GetBoolean())
        {
            var mergeCommit = await RequireMergeReceiptAsync(d, pull, ct);
            var workflowEvidence = await RequireExactWorkflowAsync(d, project, ct);
            return await new FactoryEngine(store, settings).VerifyIntegrationAsync(id, url, verifier, ct,
                remote: new GitRemoteBaseReader(synchronize: true), coordinated: true, expectedMergeCommit: mergeCommit,
                workflowEvidence: workflowEvidence);
        }

        await RequireCurrentBaseAsync(d, ct);
        if (repository.GetProperty("permissions").GetProperty("admin").GetBoolean())
            throw new InvalidOperationException("Guarded merge requires a non-admin repository credential.");
        await RequireStrictRulesAsync(d, ct);
        await RequireMergeChecksAsync(d, project, job, branch, pull, verifier, ct);
        throw new InvalidOperationException("GitHub cannot atomically enforce the signed base commit during merge.");
    }

    internal static void RequireHostPermission(PullRequestDecision d, JsonElement account, JsonElement repository)
    {
        if (account.GetProperty("login").GetString() != d.Account)
            throw new InvalidOperationException("The controller GitHub credential differs from the policy decision.");
        if (!repository.GetProperty("permissions").GetProperty("push").GetBoolean())
            throw new InvalidOperationException("The controller credential lacks repository write permission.");
    }

    private async Task RequireImmutableHeadAsync(PullRequestDecision d, string branch, CancellationToken ct)
    {
        var rules = await ReadAsync(["api", $"repos/{d.Repository}/rules/branches/{Uri.EscapeDataString(branch)}"], ct);
        foreach (var group in rules.EnumerateArray().Where(rule =>
            rule.GetProperty("ruleset_source_type").GetString() == "Repository")
            .GroupBy(rule => rule.GetProperty("ruleset_id").GetInt64()))
        {
            var types = group.Select(rule => rule.GetProperty("type").GetString()).ToHashSet();
            if (!types.Contains("update") || !types.Contains("deletion")) continue;
            if (group.Any(rule => rule.GetProperty("type").GetString() == "update" &&
                rule.GetProperty("parameters").GetProperty("update_allows_fetch_and_merge").GetBoolean())) continue;
            var detail = await ReadAsync(["api", $"repos/{d.Repository}/rulesets/{group.Key}"], ct);
            if (detail.GetProperty("enforcement").GetString() == "active" &&
                detail.TryGetProperty("bypass_actors", out var bypass) && bypass.GetArrayLength() == 0) return;
        }
        throw new InvalidOperationException("Draft publication requires a server-locked head branch without bypass actors.");
    }

    internal async Task<string> RequireMergeReceiptAsync(PullRequestDecision d, JsonElement pull, CancellationToken ct)
    {
        if (!pull.GetProperty("merged").GetBoolean())
            throw new InvalidOperationException("The signed base and head cannot be confirmed for this merge method.");
        var sha = pull.GetProperty("merge_commit_sha").GetString();
        if (sha is null || !IntegrationVerifier.IsCommitSha(sha))
            throw new InvalidOperationException("Missing merge commit.");
        var commit = await ReadAsync(["api", $"repos/{d.Repository}/git/commits/{sha}"], ct);
        if (commit.GetProperty("sha").GetString() != sha)
            throw new InvalidOperationException("GitHub returned a different merge commit.");
        var parents = commit.GetProperty("parents").EnumerateArray().Select(p => p.GetProperty("sha").GetString()).ToArray();
        if (d.Method == "merge" && parents.SequenceEqual(new[] { d.BaseCommit, d.HeadCommit })) return sha;
        if (d.Method is "squash" or "rebase" && parents.Length == 1)
        {
            // The supported linear forms require the reviewed head to include the signed base.
            var comparison = await ReadAsync(["api", $"repos/{d.Repository}/compare/{d.BaseCommit}...{d.HeadCommit}?per_page=1"], ct);
            var original = await ReadAsync(["api", $"repos/{d.Repository}/git/commits/{d.HeadCommit}"], ct);
            if (comparison.GetProperty("merge_base_commit").GetProperty("sha").GetString() == d.BaseCommit &&
                original.GetProperty("sha").GetString() == d.HeadCommit &&
                original.GetProperty("tree").GetProperty("sha").GetString() == commit.GetProperty("tree").GetProperty("sha").GetString())
            {
                var count = d.Method == "squash" ? 1 : comparison.GetProperty("total_commits").GetInt32();
                if (count is > 0 and <= 100)
                {
                    var parent = parents[0];
                    var sourceParent = original.GetProperty("parents")[0].GetProperty("sha").GetString();
                    for (var i = 1; i < count && parent is not null && IntegrationVerifier.IsCommitSha(parent); i++)
                    {
                        if (sourceParent is null || sourceParent == d.BaseCommit || !IntegrationVerifier.IsCommitSha(sourceParent))
                            throw new InvalidOperationException("The reviewed rebase lineage is incomplete.");
                        var rewritten = await ReadLinearCommitAsync(d.Repository, parent, ct);
                        var reviewed = await ReadLinearCommitAsync(d.Repository, sourceParent, ct);
                        if (rewritten.Tree != reviewed.Tree)
                            throw new InvalidOperationException("A rebased commit tree differs from its reviewed counterpart.");
                        parent = rewritten.Parent;
                        sourceParent = reviewed.Parent;
                    }
                    if (parent == d.BaseCommit &&
                        (d.Method == "squash" || (original.GetProperty("parents").GetArrayLength() == 1 && sourceParent == d.BaseCommit)))
                        return sha;
                }
            }
        }
        throw new InvalidOperationException("The resulting merge did not use the signed base and head.");
    }

    private async Task<(string? Parent, string? Tree)> ReadLinearCommitAsync(string repository, string sha, CancellationToken ct)
    {
        var commit = await ReadAsync(["api", $"repos/{repository}/git/commits/{sha}"], ct);
        var parents = commit.GetProperty("parents");
        var tree = commit.GetProperty("tree").GetProperty("sha").GetString();
        if (commit.GetProperty("sha").GetString() != sha || parents.GetArrayLength() != 1 ||
            tree is null || !IntegrationVerifier.IsCommitSha(tree))
            throw new InvalidOperationException("GitHub returned invalid rebase lineage.");
        return (parents[0].GetProperty("sha").GetString(), tree);
    }

    private async Task RequireStrictRulesAsync(PullRequestDecision d, CancellationToken ct)
    {
        var active = await ReadAsync(["api", $"repos/{d.Repository}/rules/branches/{Uri.EscapeDataString(d.BaseBranch)}"], ct);
        var strict = active.EnumerateArray().Where(rule =>
            rule.GetProperty("type").GetString() == "required_status_checks" &&
            rule.GetProperty("parameters").GetProperty("strict_required_status_checks_policy").GetBoolean()).ToArray();
        foreach (var rule in strict)
        {
            if (rule.GetProperty("ruleset_source_type").GetString() != "Repository") continue;
            var detail = await ReadAsync(["api",
                $"repos/{d.Repository}/rulesets/{rule.GetProperty("ruleset_id").GetInt64()}"], ct);
            if (detail.GetProperty("enforcement").GetString() == "active" &&
                detail.TryGetProperty("bypass_actors", out var bypass) && bypass.GetArrayLength() == 0 &&
                detail.GetProperty("rules").EnumerateArray().Any(candidate =>
                    candidate.GetProperty("type").GetString() == "pull_request" &&
                    candidate.GetProperty("parameters").GetProperty("required_approving_review_count").GetInt32() > 0 &&
                    candidate.GetProperty("parameters").GetProperty("dismiss_stale_reviews_on_push").GetBoolean())) return;
        }
        throw new InvalidOperationException("A strict required-check ruleset with no bypass actors is required.");
    }

    private async Task RequirePullRequestOnlyAsync(PullRequestDecision d, ProjectInfo project, CancellationToken ct)
    {
        // An independently signed policy names the workflow. Its hosted source must show PR-only triggers.
        if (project.Checks.Count != 1 || string.IsNullOrWhiteSpace(d.WorkflowFile) ||
            !System.Text.RegularExpressions.Regex.IsMatch(d.WorkflowFile, @"^[A-Za-z0-9_.-]+\.ya?ml$"))
            throw new InvalidOperationException("A verifiable PR-only workflow is required.");
        var rules = await ReadAsync(["api", $"repos/{d.Repository}/rules/branches/{Uri.EscapeDataString(d.BaseBranch)}"], ct);
        var required = rules.EnumerateArray().Where(rule => rule.GetProperty("type").GetString() == "required_status_checks")
            .SelectMany(rule => rule.GetProperty("parameters").GetProperty("required_status_checks").EnumerateArray()).ToArray();
        if (required.Length != 1 || required[0].GetProperty("context").GetString() != project.Checks[0].Name ||
            required[0].GetProperty("integration_id").GetInt64() != 15368)
            throw new InvalidOperationException("The PR-only check must be explicitly required from GitHub Actions.");
        var path = $".github/workflows/{d.WorkflowFile}";
        var workflow = await ReadAsync(["api", $"repos/{d.Repository}/actions/workflows/{d.WorkflowFile}"], ct);
        if (workflow.GetProperty("path").GetString() != path || workflow.GetProperty("state").GetString() != "active")
            throw new InvalidOperationException("The required workflow is not active at the authorized path.");
        var workflowId = workflow.GetProperty("id").GetInt64();
        if (workflowId != d.WorkflowId)
            throw new InvalidOperationException("The hosted workflow differs from the signed workflow ID.");
        var history = await ReadAsync(["api", $"repos/{d.Repository}/actions/workflows/{workflowId}/runs?event=pull_request&status=success&per_page=1"], ct);
        var runs = history.GetProperty("workflow_runs");
        if (runs.GetArrayLength() != 1 || runs[0].GetProperty("workflow_id").GetInt64() != workflowId ||
            runs[0].GetProperty("event").GetString() != "pull_request")
            throw new InvalidOperationException("The required workflow has no verifiable PR run.");
        var runId = runs[0].GetProperty("id").GetInt64();
        var suiteId = runs[0].GetProperty("check_suite_id").GetInt64();
        var suite = await ReadAsync(["api", $"repos/{d.Repository}/check-suites/{suiteId}/check-runs?per_page=100"], ct);
        if (!suite.GetProperty("check_runs").EnumerateArray().Any(run =>
            run.GetProperty("app").GetProperty("id").GetInt64() == 15368 &&
            run.GetProperty("name").GetString() == project.Checks[0].Name &&
            run.GetProperty("conclusion").GetString() == "success"))
            throw new InvalidOperationException($"Workflow run {runId} did not emit the required check from GitHub Actions.");
        var historySha = runs[0].GetProperty("head_sha").GetString();
        if (historySha is null || !IntegrationVerifier.IsCommitSha(historySha))
            throw new InvalidOperationException("Workflow history has an invalid source commit.");
        var historicalFile = await ReadAsync(["api", $"repos/{d.Repository}/contents/{path}?ref={historySha}"], ct);
        var hostedBlob = historicalFile.GetProperty("sha").GetString();
        foreach (var sha in new[] { d.BaseCommit, d.HeadCommit })
        {
            var file = await ReadAsync(["api", $"repos/{d.Repository}/contents/{path}?ref={sha}"], ct);
            if (hostedBlob is null || !IntegrationVerifier.IsCommitSha(hostedBlob) || file.GetProperty("sha").GetString() != hostedBlob)
                throw new InvalidOperationException("The required workflow differs from the verified PR run.");
            var yaml = Encoding.UTF8.GetString(Convert.FromBase64String(file.GetProperty("content").GetString()!));
            // Accept only the supported explicit trigger form. Other YAML forms require branch CI.
            var trigger = System.Text.RegularExpressions.Regex.Match(yaml, @"(?m)^on:[ \t]*\r?\n((?:[ \t]+[^\r\n]+\r?\n)+)");
            if (!trigger.Success ||
                !System.Text.RegularExpressions.Regex.IsMatch(trigger.Groups[1].Value,
                    @"\A  pull_request:[ \t]*\r?\n(?:    [^\r\n]+\r?\n)*\z") ||
                !System.Text.RegularExpressions.Regex.IsMatch(yaml,
                    "(?m)^jobs:[ \\t]*\\r?\\n(?:[ \\t]+[^\\r\\n]+\\r?\\n)*?  " +
                    System.Text.RegularExpressions.Regex.Escape(project.Checks[0].Name) + ":[ \\t]*\\r?$"))
                throw new InvalidOperationException("The hosted workflow does not prove the required check is PR-only.");
        }
    }

    internal async Task<string> RequireExactWorkflowAsync(PullRequestDecision d, ProjectInfo project, CancellationToken ct)
    {
        if (d.WorkflowId <= 0 || project.Checks.Count == 0)
            throw new InvalidOperationException("A signed workflow ID and named checks are required.");
        var prefix = $"repos/{d.Repository}";
        var pages = await ReadAsync(["api", "--paginate", "--slurp",
            $"{prefix}/actions/workflows/{d.WorkflowId}/runs?head_sha={d.HeadCommit}&event=pull_request&per_page=100"], ct);
        var latest = pages.EnumerateArray().SelectMany(page => page.GetProperty("workflow_runs").EnumerateArray())
            .Where(run => run.GetProperty("workflow_id").GetInt64() == d.WorkflowId &&
                run.GetProperty("head_sha").GetString() == d.HeadCommit &&
                run.GetProperty("event").GetString() == "pull_request" &&
                run.GetProperty("pull_requests").EnumerateArray().Any(pr =>
                    pr.GetProperty("number").GetInt32() == d.PullNumber &&
                    pr.GetProperty("head").GetProperty("sha").GetString() == d.HeadCommit &&
                    pr.GetProperty("base").GetProperty("sha").GetString() == d.BaseCommit))
            .OrderByDescending(run => run.GetProperty("run_number").GetInt64())
            .ThenByDescending(run => run.GetProperty("run_attempt").GetInt32()).FirstOrDefault();
        if (latest.ValueKind != JsonValueKind.Object || latest.GetProperty("status").GetString() != "completed" ||
            latest.GetProperty("conclusion").GetString() != "success")
            throw new InvalidOperationException("The latest authorized PR workflow must pass on the exact head and base.");
        var suite = latest.GetProperty("check_suite_id").GetInt64();
        var runId = latest.GetProperty("id").GetInt64();
        var attempt = latest.GetProperty("run_attempt").GetInt32();
        var jobs = await ReadAsync(["api", "--paginate", "--slurp",
            $"{prefix}/actions/runs/{runId}/attempts/{attempt}/jobs?per_page=100"], ct);
        var hostedJobs = jobs.EnumerateArray().SelectMany(page => page.GetProperty("jobs").EnumerateArray()).ToArray();
        var runs = await ReadAsync(["api", "--paginate", "--slurp",
            $"{prefix}/check-suites/{suite}/check-runs?per_page=100"], ct);
        var checks = runs.EnumerateArray().SelectMany(page => page.GetProperty("check_runs").EnumerateArray()).ToArray();
        var checkIds = new List<long>();
        foreach (var check in project.Checks)
        {
            var candidates = hostedJobs.Where(job => job.GetProperty("name").GetString() == check.Name).ToArray();
            if (candidates.Length != 1 || candidates[0].GetProperty("conclusion").GetString() != "success")
                throw new InvalidOperationException("The authorized workflow attempt did not pass: " + check.Name);
            var link = candidates[0].GetProperty("check_run_url").GetString();
            var match = checks.Where(run => run.GetProperty("url").GetString() == link &&
                run.GetProperty("app").GetProperty("id").GetInt64() == 15368 &&
                run.GetProperty("head_sha").GetString() == d.HeadCommit &&
                run.GetProperty("name").GetString() == check.Name &&
                run.GetProperty("conclusion").GetString() == "success").ToArray();
            if (match.Length != 1)
                throw new InvalidOperationException("The workflow check did not originate from the trusted app on the exact head: " + check.Name);
            checkIds.Add(match[0].GetProperty("id").GetInt64());
        }
        return Json.Stringify(new { d.WorkflowId, runId, attempt, suite, d.PullNumber, d.HeadCommit, d.BaseCommit, checkIds });
    }

    private void RequireCurrentDecision(SignedPullRequestDecision signed, Guid id)
    {
        var d = signed.Decision;
        ValidateSignature(signed);
        var job = store.Job(id);
        ValidateTarget(d, job, store.Read().Projects.Single(p => p.Id == job.ProjectId));
        if (!settings.AllowPush || (d.Action == "merge" && !settings.AutoMerge) || !store.IsReviewApproved(id))
            throw new InvalidOperationException("Publication authorization changed during verification.");
    }

    private async Task RequireBranchChecksAsync(PullRequestDecision d, ProjectInfo project, CancellationToken ct)
    {
        var pages = await ReadAsync(["api", "--paginate", "--slurp",
            $"repos/{d.Repository}/commits/{d.HeadCommit}/check-runs?per_page=100"], ct);
        var runs = pages.EnumerateArray().SelectMany(page => page.GetProperty("check_runs").EnumerateArray()).ToArray();
        var rules = await ReadAsync(["api", $"repos/{d.Repository}/rules/branches/{Uri.EscapeDataString(d.BaseBranch)}"], ct);
        var required = rules.EnumerateArray().Where(rule => rule.GetProperty("type").GetString() == "required_status_checks")
            .SelectMany(rule => rule.GetProperty("parameters").GetProperty("required_status_checks").EnumerateArray()).ToArray();
        if (required.Length == 0 || project.Checks.Any(check => !required.Any(rule => rule.GetProperty("context").GetString() == check.Name)))
            throw new InvalidOperationException("Branch CI policy cannot be verified.");
        foreach (var rule in required)
        {
            var name = rule.GetProperty("context").GetString();
            if (!rule.TryGetProperty("integration_id", out var app) || app.ValueKind != JsonValueKind.Number ||
                runs.Where(run => run.GetProperty("name").GetString() == name &&
                    run.GetProperty("head_sha").GetString() == d.HeadCommit &&
                    run.GetProperty("app").GetProperty("id").GetInt64() == app.GetInt64())
                    .OrderByDescending(run => run.GetProperty("id").GetInt64()).FirstOrDefault() is not
                    { ValueKind: JsonValueKind.Object } latest ||
                latest.GetProperty("conclusion").GetString() != "success")
                throw new InvalidOperationException("Trusted branch CI is missing or unsuccessful: " + name);
        }
    }

    internal async Task RequireMergeChecksAsync(PullRequestDecision d, ProjectInfo project, FactoryJob job, string branch,
        JsonElement pull, GitHubWorkerResultVerifier verifier, CancellationToken ct)
    {
        using var snapshot = JsonDocument.Parse(pull.GetRawText());
        GitHubWorkerResultVerifier.ValidatePull(snapshot, d.Repository, d.BaseBranch, branch, d.HeadCommit);
        if (pull.GetProperty("draft").GetBoolean() || pull.GetProperty("mergeable_state").GetString() != "clean" ||
            pull.GetProperty("base").GetProperty("sha").GetString() != d.BaseCommit)
            throw new InvalidOperationException("The PR is not clean on the authorized base.");
        await RequireExactWorkflowAsync(d, project, ct);
        var checks = await verifier.RequiredChecksAsync(d.Repository, d.PullNumber, d.BaseBranch, d.HeadCommit, ct);
        if (!project.Checks.All(check => checks.Contains(check.Name)))
            throw new InvalidOperationException("A configured required check is missing.");
        var changed = await verifier.CompareFilePathsAsync(d.Repository, d.BaseCommit, d.HeadCommit, ct);
        WorkPackageScope.Enforce(job.ExpectedPaths, changed);
        GitOperations.EnforcePaths(changed, project.ProtectedPaths);
        var author = pull.GetProperty("user").GetProperty("login").GetString();
        var pages = await ReadAsync(["api", "--paginate", "--slurp",
            $"repos/{d.Repository}/pulls/{d.PullNumber}/reviews?per_page=100"], ct);
        var reviews = pages.EnumerateArray().SelectMany(page => page.EnumerateArray())
            .Where(review => review.GetProperty("state").GetString() != "COMMENTED")
            .OrderBy(review => review.GetProperty("id").GetInt64())
            .GroupBy(review => review.GetProperty("user").GetProperty("login").GetString(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in reviews)
        {
            var review = group.Last();
            if (string.IsNullOrWhiteSpace(group.Key) || string.Equals(group.Key, author, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(group.Key, d.Account, StringComparison.OrdinalIgnoreCase) ||
                review.GetProperty("state").GetString() != "APPROVED" ||
                review.GetProperty("commit_id").GetString() != d.HeadCommit) continue;
            var permission = await ReadAsync(["api",
                $"repos/{d.Repository}/collaborators/{Uri.EscapeDataString(group.Key)}/permission"], ct);
            if (permission.GetProperty("permission").GetString() is "write" or "maintain" or "admin") return;
        }
        throw new InvalidOperationException("An independent GitHub approval on the exact head is required.");
    }

    private static void ValidateSignature(SignedPullRequestDecision signed)
    {
        var key = Environment.GetEnvironmentVariable("FACTORY_INTEGRATION_PUBLIC_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No project integration authority is configured.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key);
        if (!rsa.VerifyData(Encoding.UTF8.GetBytes(Json.Stringify(signed.Decision)),
            Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidOperationException("Project integration decision has an invalid signature.");
    }

    private static void ValidateTarget(PullRequestDecision d, FactoryJob job, ProjectInfo project)
    {
        var now = DateTimeOffset.UtcNow;
        if (d.JobId != job.Id || d.ProjectId != project.Id || d.ProjectRevision != ReviewPolicy.ProjectRevision(project) ||
            d.Repository != GitHubWorkerResultVerifier.GitHubRepository(project.Origin) || d.BaseBranch != project.BaseBranch ||
            d.ScopeRevision != ScopeRevision(job) || d.HeadCommit != job.HeadSha || !IntegrationVerifier.IsCommitSha(d.HeadCommit) ||
            !IntegrationVerifier.IsCommitSha(d.BaseCommit) || d.ExpiresAt <= now || d.ExpiresAt > now.AddHours(24) ||
            (d.Action == "draft" ? d.PullNumber != 0 || d.Method != "pr-ci" :
                d.PullNumber <= 0 || d.Method is not ("merge" or "squash" or "rebase")))
            throw new InvalidOperationException("The signed policy decision does not match the current job and target.");
    }

    internal static string ScopeRevision(FactoryJob job) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Stringify(job.ExpectedPaths))));

    private void Reserve(SignedPullRequestDecision signed, FactoryJob before, ProjectInfo project)
    {
        var d = signed.Decision;
        store.Change((state, db, tx) =>
        {
            var current = state.Jobs.Single(j => j.Id == before.Id);
            var currentProject = state.Projects.Single(p => p.Id == project.Id);
            ValidateTarget(d, current, currentProject);
            if (current.ControlRevision != before.ControlRevision ||
                !StateStore.IsReviewApproved(db, tx, current, currentProject))
                throw new InvalidOperationException("Job or approval changed before the operation.");
            var reference = d.Action + ":" + d.HeadCommit;
            using var read = db.CreateCommand();
            read.Transaction = tx;
            read.CommandText = """
                SELECT 1 FROM lifecycle_audit WHERE entity_id=$id AND entity_kind='publication'
                    AND reference=$reference LIMIT 1;
                """;
            read.Parameters.AddWithValue("$id", current.Id.ToString());
            read.Parameters.AddWithValue("$reference", reference);
            if (read.ExecuteScalar() is not null)
                throw new InvalidOperationException("A previous operation requires reconciliation; automatic replay is disabled.");
            StateStore.RecordLifecycle(db, tx, current.ProjectId, "publication", current.Id, d.Account, current.State,
                "reserved", LifecycleReason.PullRequestPrepared, reference, current.ExecutionGeneration, state);
            StateStore.RecordLifecycle(db, tx, current.ProjectId, "publication-decision", current.Id, d.Account,
                current.State, "authorized", LifecycleReason.ApprovalGranted, Json.Stringify(signed), current.ExecutionGeneration, state);
            return true;
        });
    }

    private async Task RequireCurrentBaseAsync(PullRequestDecision d, CancellationToken ct)
    {
        var branch = await ReadAsync(["api", $"repos/{d.Repository}/git/ref/heads/{Uri.EscapeDataString(d.BaseBranch)}"], ct);
        if (branch.GetProperty("object").GetProperty("sha").GetString() != d.BaseCommit)
            throw new InvalidOperationException("The remote base changed.");
    }

    private Task<JsonElement> ReadPullAsync(PullRequestDecision d, CancellationToken ct) =>
        ReadAsync(["api", $"repos/{d.Repository}/pulls/{d.PullNumber.ToString(CultureInfo.InvariantCulture)}"], ct);

    private async Task<JsonElement> ReadAsync(string[] args, CancellationToken ct)
    {
        var result = await github(args, 60, ct);
        using var json = JsonDocument.Parse(Commands.Require(result, "read GitHub evidence"));
        return json.RootElement.Clone();
    }

    internal async Task<JsonElement> MutateAsync(string[] args, CancellationToken ct)
    {
        var result = await github(args, 60, ct);
        if (!result.Ok)
            throw new InvalidOperationException("GitHub mutation was refused or interrupted. Reconcile the reserved operation.");
        using var json = JsonDocument.Parse(result.Output);
        if (json.RootElement.TryGetProperty("merged", out var merged) && !merged.GetBoolean())
            throw new InvalidOperationException("GitHub did not merge the pull request.");
        return json.RootElement.Clone();
    }
}
