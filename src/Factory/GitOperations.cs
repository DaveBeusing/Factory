using System.Security.Cryptography;
using System.Text;

namespace Factory;

public sealed class GitOperations
{
    private readonly string _workspace;
    public GitOperations(string workspace)
    {
        _workspace = Path.GetFullPath(workspace);
        Directory.CreateDirectory(_workspace);
    }
    public static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static string RepositoryRoot(string folder)
    {
        var abs = Path.GetFullPath(folder);
        var root = Commands.Require(Commands.Run("git", ["-C", abs, "rev-parse", "--show-toplevel"], Environment.CurrentDirectory), "git repository root");
        if (!SamePath(abs, root)) throw new ArgumentException($"Expected Git repository root: {root}");
        return Path.GetFullPath(root);
    }
    public static string Git(string dir, params string[] args) => Commands.Require(
        Commands.Run("git", ["-C", dir, ..args], Environment.CurrentDirectory), "git " + string.Join(' ', args));
    public static string? Origin(string root)
    {
        var r = Commands.Run("git", ["-C", root, "remote", "get-url", "origin"], Environment.CurrentDirectory);
        return r.Ok ? r.Output.Trim() : null;
    }
    public string Prepare(ProjectInfo project, FactoryJob job, string? rootOverride = null)
    {
        WorkPackagePreflight.RequireMaster(project);
        var repo = rootOverride ?? project.Path;
        var masterCommit = WorkPackagePreflight.MasterCommit(
            new ProjectInfo { Path = repo, BaseBranch = "master" });
        var origin = WorkPackagePreflight.ConfiguredOrigin(project, repo);
        var (_, remoteStatus) = WorkPackagePreflight.InspectRemoteMaster(repo, origin, masterCommit);
        WorkPackagePreflight.RequireCurrentMaster(remoteStatus);
        var path = Path.Combine(_workspace, job.Id.ToString("N"));
        var branch = string.IsNullOrEmpty(job.Branch) ? $"factory/{job.Id:N}" : job.Branch;
        if (Directory.Exists(path) && (File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git"))))
        {
            // Never discard an existing partial implementation. Ensure it belongs to
            // this job branch and still contains the current master base.
            var currentBranch = Git(path, "branch", "--show-current").Trim();
            if (currentBranch != branch ||
                !Commands.Run("git", ["-C", path, "merge-base", "--is-ancestor", masterCommit, "HEAD"],
                    Environment.CurrentDirectory).Ok)
                throw new InvalidOperationException(
                    "Existing job worktree differs from current master or expected branch. Inspect and reconcile; do not overwrite it.");
            return path;
        }
        var branchFound = Commands.Run("git", ["-C", repo, "show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], Environment.CurrentDirectory).Ok;
        if (branchFound && !Commands.Run("git",
            ["-C", repo, "merge-base", "--is-ancestor", masterCommit, branch],
            Environment.CurrentDirectory).Ok)
            throw new InvalidOperationException(
                "Existing work-package branch is not based on current master. Review and rebase it explicitly; do not discard existing code.");
        var args = branchFound
            ? new[] { "-C", repo, "worktree", "add", path, branch }
            : new[] { "-C", repo, "worktree", "add", "-b", branch, path, masterCommit };
        Commands.Require(Commands.Run("git", args, Environment.CurrentDirectory, 120), "git worktree add");
        return path;
    }
    public static string Head(string path) => Git(path, "rev-parse", "HEAD");
    public static IReadOnlyList<string> ChangedFiles(string path, string target)
    {
        var r = Commands.Run("git", ["-C", path, "diff", "--name-only", $"{target}...HEAD"], Environment.CurrentDirectory);
        if (!r.Ok) throw new InvalidOperationException(r.Error);
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
    public static void EnforcePaths(IEnumerable<string> files, IEnumerable<string> protectedPaths)
    {
        foreach (var file in files)
        {
            var normalized = file.Replace('\\', '/').TrimStart('/');
            foreach (var p in protectedPaths)
            {
                var prefix = p.Replace('\\', '/').TrimStart('/');
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Protected path modified: {file}");
            }
        }
    }
    public static string CommitIfNeeded(string path, string title)
    {
        var status = Git(path, "status", "--porcelain");
        if (string.IsNullOrWhiteSpace(status)) return Head(path);
        Git(path, "add", "--all");
        Git(path, "commit", "-m", "Factory: " + title[..Math.Min(70, title.Length)]);
        return Head(path);
    }
    public static bool IsIntegrated(ProjectInfo p, FactoryJob j)
    {
        var reference = string.IsNullOrEmpty(j.HeadSha) ? j.Branch : j.HeadSha;
        if (string.IsNullOrEmpty(reference)) return false;
        var result = Commands.Run("git", ["-C", p.Path, "merge-base", "--is-ancestor", reference, p.BaseBranch], Environment.CurrentDirectory);
        return result.Code == 0;
    }
    public static bool CanMerge(ProjectInfo p, FactoryJob j)
    {
        if (string.IsNullOrEmpty(j.Branch)) return false;
        var result = Commands.Run("git", ["-C", p.Path, "merge-tree", "--write-tree", p.BaseBranch, j.Branch], Environment.CurrentDirectory);
        return result.Ok;
    }
    public static async Task<string> PublishAsync(ProjectInfo project, FactoryJob job, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.Worktree) || string.IsNullOrEmpty(project.Origin))
            throw new InvalidOperationException("Remote PR requires a worktree and Git origin");
        var push = await Commands.RunAsync("git", ["-C", job.Worktree, "push", "-u", "origin", job.Branch],
            Environment.CurrentDirectory, 120, cancellationToken: ct);
        Commands.Require(push, "git push");
        var prs = await Commands.RunAsync("gh", ["pr", "list", "--head", job.Branch, "--json", "url", "--jq", ".[0].url"],
            job.Worktree, 40, cancellationToken: ct);
        if (prs.Ok && !string.IsNullOrWhiteSpace(prs.Output)) return prs.Output.Trim();
        var created = await Commands.RunAsync("gh", ["pr", "create", "--draft", "--base", project.BaseBranch,
            "--head", job.Branch, "--title", job.Title, "--body", "Factory job: " + job.Id],
            job.Worktree, 60, cancellationToken: ct);
        return Commands.Require(created, "gh pr create");
    }
}
