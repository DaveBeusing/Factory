using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Factory;

public sealed record PullRequestDecision(Guid ProjectId, Guid JobId, string ProjectRevision, string Repository,
    string BaseBranch, string BaseCommit, string HeadCommit, string Account, string Action, string Method,
    int PullNumber, DateTimeOffset ExpiresAt, string ScopeRevision);
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
        if (account.GetProperty("login").GetString() != d.Account)
            throw new InvalidOperationException("The controller GitHub credential differs from the policy decision.");
        var repository = await ReadAsync(["api", $"repos/{d.Repository}"], ct);
        if (!repository.GetProperty("permissions").GetProperty("push").GetBoolean())
            throw new InvalidOperationException("The controller credential lacks repository write permission.");
        if (d.Action == "draft")
        {
            await RequireCurrentBaseAsync(d, ct);
            var existing = await ReadAsync(["api", $"repos/{d.Repository}/pulls?state=all&head={Uri.EscapeDataString(d.Repository.Split('/')[0] + ":" + branch)}"], ct);
            if (existing.GetArrayLength() != 0)
                throw new InvalidOperationException("An existing pull request requires reconciliation.");
            if (job.Mode != JobExecutionModes.Local || GitOperations.Git(job.Worktree, "status", "--porcelain").Length != 0)
                throw new InvalidOperationException("Draft publication requires a clean controller worktree.");
            WorkPackageScope.ValidateWorktree(project, job);
            Reserve(signed, job, project);
            await RequireCurrentBaseAsync(d, ct);
            var push = await Commands.RunAsync("git", ["-C", job.Worktree, "push", project.Origin,
                $"--force-with-lease=refs/heads/{branch}:", $"{d.HeadCommit}:refs/heads/{branch}"],
                Environment.CurrentDirectory, 120, cancellationToken: ct);
            Commands.Require(push, "publish approved commit");
            var created = await MutateAsync(["api", "--method", "POST", $"repos/{d.Repository}/pulls",
                "-f", "head=" + branch, "-f", "base=" + d.BaseBranch, "-f", "title=" + job.Title,
                "-F", "draft=true", "-f", "body=Required PR CI evaluation for Factory job " + id], ct);
            return new { url = created.GetProperty("html_url").GetString(), d.HeadCommit, draft = true };
        }

        var url = $"https://github.com/{d.Repository}/pull/{d.PullNumber}";
        var verifier = new GitHubWorkerResultVerifier(github);
        var pull = await ReadPullAsync(d, ct);
        if (pull.GetProperty("merged").GetBoolean())
        {
            RequireReservation(d);
            return await new FactoryEngine(store, settings).VerifyIntegrationAsync(id, url, verifier, ct, coordinated: true);
        }

        await RequireCurrentBaseAsync(d, ct);
        await RequireMergeChecksAsync(d, project, job, branch, pull, verifier, ct);
        Reserve(signed, job, project);
        await RequireCurrentBaseAsync(d, ct);
        await RequireMergeChecksAsync(d, project, job, branch, await ReadPullAsync(d, ct), verifier, ct);
        await RequireCurrentBaseAsync(d, ct);
        await MutateAsync(["api", "--method", "PUT", $"repos/{d.Repository}/pulls/{d.PullNumber}/merge",
            "-f", "sha=" + d.HeadCommit, "-f", "merge_method=" + d.Method], ct);
        return await new FactoryEngine(store, settings).VerifyIntegrationAsync(id, url, verifier, ct, coordinated: true);
    }

    private async Task RequireMergeChecksAsync(PullRequestDecision d, ProjectInfo project, FactoryJob job, string branch,
        JsonElement pull, GitHubWorkerResultVerifier verifier, CancellationToken ct)
    {
        using var snapshot = JsonDocument.Parse(pull.GetRawText());
        GitHubWorkerResultVerifier.ValidatePull(snapshot, d.Repository, d.BaseBranch, branch, d.HeadCommit);
        if (pull.GetProperty("draft").GetBoolean() || pull.GetProperty("mergeable_state").GetString() != "clean" ||
            pull.GetProperty("base").GetProperty("sha").GetString() != d.BaseCommit)
            throw new InvalidOperationException("The PR is not clean on the authorized base.");
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

    private void RequireReservation(PullRequestDecision d) =>
        store.Change((_, db, tx) =>
        {
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = """
                SELECT reference FROM lifecycle_audit WHERE entity_id=$id AND entity_kind='publication-decision';
                """;
            query.Parameters.AddWithValue("$id", d.JobId.ToString());
            using var rows = query.ExecuteReader();
            while (rows.Read())
            {
                var prior = JsonSerializer.Deserialize<SignedPullRequestDecision>(rows.GetString(0), Json.Options)?.Decision;
                if (prior is not null && (prior with { ExpiresAt = d.ExpiresAt }) == d) return true;
            }
            throw new InvalidOperationException("No matching merge reservation exists.");
        });

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

    private async Task<JsonElement> MutateAsync(string[] args, CancellationToken ct)
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
