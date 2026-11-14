using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory;

// The configured remote, not a locally writable base branch, is authoritative for remote projects.
public sealed record IntegrationProof(string Source, string BaseCommit, string IntegratedCommit);

public sealed record MergedPullRequest(
    bool Merged, string BaseRepository, string HeadRepository,
    string BaseBranch, string HeadBranch, string HeadCommit, string MergeCommit);

public interface IIntegrationPullRequestReader
{
    Task<MergedPullRequest> ReadAsync(string repository, int number, CancellationToken ct);
}

public sealed class GitHubIntegrationPullRequestReader : IIntegrationPullRequestReader
{
    public async Task<MergedPullRequest> ReadAsync(string repository, int number, CancellationToken ct)
    {
        var result = await Commands.RunAsync("gh",
            ["api", $"repos/{repository}/pulls/{number}"],
            Environment.CurrentDirectory, 45, cancellationToken: ct);
        if (!result.Ok)
            throw new InvalidOperationException("Unable to verify merged pull request against GitHub");
        using var response = JsonDocument.Parse(result.Output);
        var pr = response.RootElement;
        return new MergedPullRequest(
            pr.GetProperty("merged").GetBoolean(),
            pr.GetProperty("base").GetProperty("repo").GetProperty("full_name").GetString() ?? "",
            pr.GetProperty("head").GetProperty("repo").GetProperty("full_name").GetString() ?? "",
            pr.GetProperty("base").GetProperty("ref").GetString() ?? "",
            pr.GetProperty("head").GetProperty("ref").GetString() ?? "",
            pr.GetProperty("head").GetProperty("sha").GetString() ?? "",
            pr.GetProperty("merge_commit_sha").GetString() ?? "");
    }
}

public interface IRemoteBaseReader
{
    Task<string> ReadBaseCommitAsync(ProjectInfo project, CancellationToken ct);
}

public sealed class GitRemoteBaseReader : IRemoteBaseReader
{
    public async Task<string> ReadBaseCommitAsync(ProjectInfo project, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(project.Origin) || string.IsNullOrWhiteSpace(project.BaseBranch))
            throw new InvalidOperationException("Remote origin and base branch are required");

        // A unique, short-lived ref prevents concurrent verifications from racing on FETCH_HEAD.
        // Do not change the checked-out branch, stage files, or write to origin.
        var temporaryRef = "refs/factory/verify/" + Guid.NewGuid().ToString("N");
        try
        {
            var fetch = await Commands.RunAsync("git",
                ["-C", project.Path, "fetch", "--no-tags", "--no-recurse-submodules",
                 project.Origin, $"+refs/heads/{project.BaseBranch}:{temporaryRef}"],
                Environment.CurrentDirectory, 120, cancellationToken: ct);
            if (!fetch.Ok)
                throw new InvalidOperationException("Could not fetch the configured remote base; integration remains unverified");

            var sha = GitOperations.Git(project.Path, "rev-parse", "--verify", temporaryRef + "^{commit}").Trim();
            if (!IntegrationVerifier.IsCommitSha(sha))
                throw new InvalidOperationException("Invalid remote base commit");
            return sha;
        }
        finally
        {
            // This is the ref owned by this verification only. Never clean unrelated refs.
            Commands.Run("git", ["-C", project.Path, "update-ref", "-d", temporaryRef],
                Environment.CurrentDirectory);
        }
    }
}

public static class IntegrationVerifier
{
    public static bool IsCommitSha(string value)
        => !string.IsNullOrWhiteSpace(value) &&
            Regex.IsMatch(value, "^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$");

    public static bool IsAncestor(string repositoryPath, string ancestor, string descendant)
        => IsCommitSha(ancestor) && !string.IsNullOrWhiteSpace(descendant) &&
            Commands.Run("git", ["-C", repositoryPath, "merge-base", "--is-ancestor",
                ancestor, descendant], Environment.CurrentDirectory).Code == 0;

    public static void ValidateMergedPullRequest(MergedPullRequest pr, string repository,
        ProjectInfo project, FactoryJob job)
    {
        if (!pr.Merged ||
            !string.Equals(pr.BaseRepository, repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pr.HeadRepository, repository, StringComparison.OrdinalIgnoreCase) ||
            pr.BaseBranch != project.BaseBranch ||
            string.IsNullOrWhiteSpace(job.Branch) || pr.HeadBranch != job.Branch ||
            !string.Equals(pr.HeadCommit, job.HeadSha, StringComparison.OrdinalIgnoreCase) ||
            !IsCommitSha(pr.MergeCommit))
            throw new InvalidOperationException(
                "Pull request merge, repository, base, source branch or source commit was not verified");
    }

    public static async Task<IntegrationProof> VerifyAsync(ProjectInfo project, FactoryJob job,
        IRemoteBaseReader? remote = null, IIntegrationPullRequestReader? pullRequests = null,
        CancellationToken ct = default)
    {
        if (!IsCommitSha(job.HeadSha))
            throw new InvalidOperationException("An exact committed source SHA is required for integration verification");
        if (job.Mode == JobExecutionModes.Remote && string.IsNullOrWhiteSpace(job.PrUrl))
            throw new InvalidOperationException("Remote integration requires a merged pull request.");

        if (string.IsNullOrWhiteSpace(project.Origin))
        {
            if (job.Mode != JobExecutionModes.Local || !string.IsNullOrWhiteSpace(job.PrUrl))
                throw new InvalidOperationException("Remote job or PR cannot be verified without a configured origin");
            if (!IsAncestor(project.Path, job.HeadSha, project.BaseBranch))
                throw new InvalidOperationException("Source commit is not integrated into the local-only base branch");
            return new IntegrationProof("local-only", GitOperations.Git(project.Path,
                "rev-parse", project.BaseBranch).Trim(), job.HeadSha);
        }

        var baseSha = await (remote ?? new GitRemoteBaseReader()).ReadBaseCommitAsync(project, ct);
        if (!IsCommitSha(baseSha))
            throw new InvalidOperationException("Remote provider returned an invalid base commit");

        string incorporatedSha;
        string source;
        if (!string.IsNullOrWhiteSpace(job.PrUrl))
        {
            // Squash and rebase merges can replace the original source SHA. Require GitHub's
            // authoritative merged PR metadata, then verify its merge commit in the remote base.
            var repository = GitHubWorkerResultVerifier.GitHubRepository(project.Origin);
            var number = GitHubWorkerResultVerifier.PullNumber(job.PrUrl, repository);
            var pr = await (pullRequests ?? new GitHubIntegrationPullRequestReader())
                .ReadAsync(repository, number, ct);
            ValidateMergedPullRequest(pr, repository, project, job);
            incorporatedSha = pr.MergeCommit;
            source = "remote-merged-pr";
        }
        else
        {
            incorporatedSha = job.HeadSha;
            source = "remote-ancestry";
        }

        if (!IsAncestor(project.Path, incorporatedSha, baseSha))
            throw new InvalidOperationException("The integration commit is not reachable from the remote base branch");

        // Downstream worktrees currently start from the local base branch. Do not release
        // dependencies until that checkout contains the verified remote base revision.
        if (!IsAncestor(project.Path, baseSha, project.BaseBranch))
            throw new InvalidOperationException(
                "Remote integration is confirmed, but the local base must be updated before dependent jobs can start");
        return new IntegrationProof(source, baseSha, incorporatedSha);
    }
}
