using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory;

/// <summary>
/// Captures bounded, source-commit-bound evidence before a goal is divided into work packages.
/// A keyword match is a candidate for inspection, never proof of feature completeness.
/// </summary>
public sealed class WorkPackagePreflightReport
{
    public Guid ProjectId { get; set; }
    public Guid GoalId { get; set; }
    public string BaseBranch { get; set; } = "master";
    public string BaseCommit { get; set; } = "";
    public string? RemoteMasterCommit { get; set; }
    public string RemoteMasterStatus { get; set; } = "not-checked";
    public string? GitHubRepository { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> SearchTerms { get; set; } = [];
    public List<string> MasterFiles { get; set; } = [];
    public List<ExistingCodeCandidate> ExistingCode { get; set; } = [];
    public List<ExistingWorkCandidate> ExistingJobs { get; set; } = [];
    public List<OpenWorkCandidate> OpenPullRequests { get; set; } = [];
    public bool PullRequestsChecked { get; set; }
    public string PullRequestCheckNote { get; set; } = "";
    public List<OpenWorkCandidate> OpenIssues { get; set; } = [];
    public bool IssuesChecked { get; set; }
    public string IssueCheckNote { get; set; } = "";
}

public sealed class ExistingCodeCandidate
{
    public string Path { get; set; } = "";
    public string Sample { get; set; } = "";
}

public sealed class ExistingWorkCandidate
{
    public Guid JobId { get; set; }
    public string Title { get; set; } = "";
    public string State { get; set; } = "";
    public bool Integrated { get; set; }
}

public sealed class OpenWorkCandidate
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string HeadBranch { get; set; } = "";
    public string HeadCommit { get; set; } = "";
    public string Url { get; set; } = "";
}

public static class WorkPackagePreflight
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "again", "also", "and", "are", "before", "build", "code", "create",
        "das", "den", "der", "die", "eine", "einen", "einer", "enhance", "existing", "feature",
        "for", "from", "have", "implement", "implementation", "make", "more", "need",
        "nicht", "noch", "project", "software", "that", "this", "und", "update", "with",
        "work", "would", "werden", "soll", "sollen", "wir", "the", "user", "when"
    };

    public static void RequireMaster(ProjectInfo project)
    {
        if (project.BaseBranch != "master")
            throw new InvalidOperationException(
                "Work packages must be based on master. Reconfigure the project base branch before planning or executing.");
    }

    public static string MasterCommit(ProjectInfo project)
    {
        RequireMaster(project);
        // Resolve the actual branch reference, never HEAD from a feature checkout.
        var commit = GitOperations.Git(project.Path, "rev-parse", "--verify", "refs/heads/master^{commit}").Trim();
        if (!IntegrationVerifier.IsCommitSha(commit))
            throw new InvalidOperationException("master did not resolve to a commit");
        return commit;
    }

    public static WorkPackagePreflightReport Inspect(
        FactoryState state, ProjectInfo project, FactoryGoal goal)
    {
        if (goal.ProjectId != project.Id || !state.Projects.Any(p => p.Id == project.Id))
            throw new InvalidOperationException("Goal must belong to a registered project");
        var commit = MasterCommit(project);
        var terms = Terms(goal);
        var report = new WorkPackagePreflightReport
        {
            ProjectId = project.Id, GoalId = goal.Id,
            BaseCommit = commit, SearchTerms = terms
        };

        var origin = ConfiguredOrigin(project);
        (report.RemoteMasterCommit, report.RemoteMasterStatus) = InspectRemoteMaster(project.Path, origin, commit);

        var tree = Commands.Run("git", ["-C", project.Path, "ls-tree", "-r", "--name-only", commit], project.Path, 60);
        if (!tree.Ok) throw new InvalidOperationException("Cannot inspect master source tree: " + tree.Error);
        report.MasterFiles = tree.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(file => !file.StartsWith(".git/", StringComparison.Ordinal))
            .Take(180).ToList();

        report.ExistingJobs = state.Jobs
            .Where(j => j.ProjectId == project.Id && terms.Any(term =>
                j.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                j.Description.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(j => j.UpdatedAt)
            .Take(20)
            .Select(j => new ExistingWorkCandidate
            {
                JobId = j.Id, Title = j.Title, State = j.State, Integrated = j.Integrated
            }).ToList();

        if (terms.Count > 0)
        {
            // Search immutable master tree content, not the checkout's current feature branch
            // or uncommitted files. Use argument arrays rather than shell interpolation.
            var args = new List<string> { "-C", project.Path, "grep", "-n", "-i", "-F" };
            foreach (var term in terms) { args.Add("-e"); args.Add(term); }
            args.Add(commit);
            args.Add("--");
            args.Add("*.cs"); args.Add("*.csproj"); args.Add("*.js");
            args.Add("*.ts"); args.Add("*.tsx"); args.Add("*.md");
            args.Add("*.json"); args.Add("*.py"); args.Add("*.cpp"); args.Add("*.h");
            var found = Commands.Run("git", args, project.Path, 60);
            if (found.Code is not (0 or 1))
                throw new InvalidOperationException("Cannot verify existing source on master: " + found.Error);
            report.ExistingCode = found.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => ParseMatch(commit, line))
                .Where(match => match != null)
                .Cast<ExistingCodeCandidate>()
                .GroupBy(x => x.Path, StringComparer.Ordinal)
                .Take(30)
                .Select(g => g.First())
                .ToList();
        }

        report.GitHubRepository = GitHubRepository(origin);
        (report.OpenPullRequests, report.PullRequestsChecked, report.PullRequestCheckNote) =
            InspectOpenWork(project, terms, report.GitHubRepository, pullRequests: true);
        (report.OpenIssues, report.IssuesChecked, report.IssueCheckNote) =
            InspectOpenWork(project, terms, report.GitHubRepository, pullRequests: false);
        return report;
    }

    internal static string? ConfiguredOrigin(ProjectInfo project, string? repositoryPath = null) =>
        string.IsNullOrWhiteSpace(project.Origin) ? GitOperations.Origin(repositoryPath ?? project.Path) : project.Origin;

    internal static (string? Commit, string Status) InspectRemoteMaster(string directory, string? origin, string localCommit)
    {
        if (string.IsNullOrWhiteSpace(origin)) return (null, "local-only");
        try
        {
            var result = Commands.Run("git", ["-C", directory, "-c", "credential.interactive=false",
                "ls-remote", "--exit-code", "--", origin, "refs/heads/master"], directory, 12);
            if (result.Code == 2) return (null, "missing");
            if (result.Ok)
            {
                var fields = result.Output.Trim().Split('\t');
                if (fields is [var remote, "refs/heads/master"] && IntegrationVerifier.IsCommitSha(remote))
                    return (remote, string.Equals(remote, localCommit, StringComparison.OrdinalIgnoreCase)
                        ? "matched" : "mismatch");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or IOException) { }
        return (null, "unavailable");
    }

    internal static void RequireCurrentMaster(string status)
    {
        if (status == "mismatch")
            throw new InvalidOperationException("Local master differs from configured remote master; update the checkout and refresh preflight");
        if (status == "missing")
            throw new InvalidOperationException("Configured remote has no master branch; check the project origin before continuing");
    }

    internal static string? GitHubRepository(string? origin)
    {
        if (origin is null) return null;
        try { return GitHubWorkerResultVerifier.GitHubRepository(origin); }
        catch (InvalidOperationException) { return null; }
    }

    internal static IEnumerable<(int Number, string HeadCommit)> PullRequestRevisions(WorkPackagePreflightReport report) =>
        report.OpenPullRequests.OrderBy(p => p.Number).Select(p => (p.Number, p.HeadCommit));

    private static (List<OpenWorkCandidate> Matches, bool Checked, string Note) InspectOpenWork(
        ProjectInfo project, List<string> terms, string? repository, bool pullRequests)
    {
        var label = pullRequests ? "Open PR" : "Open issue";
        if (repository is null) return ([], false, label + " inventory not applicable or origin unavailable.");
        if (!pullRequests)
        {
            var (issues, complete, note) = DiscoverIssues(project, repository);
            return (issues.Where(item => terms.Any(term => item.Title.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Select(item => new OpenWorkCandidate { Number = item.Number, Title = item.Title, Url = item.Url })
                .ToList(), complete, note);
        }
        const int limit = 100;
        var qualifiedRepository = "github.com/" + repository;
        var args = pullRequests
            ? new[] { "pr", "list", "--repo", qualifiedRepository, "--state", "open", "--base", "master",
                "--json", "number,title,headRefName,headRefOid,url", "--limit", limit.ToString() }
            : new[] { "issue", "list", "--repo", qualifiedRepository, "--state", "open",
                "--json", "number,title,url", "--limit", limit.ToString() };
        try
        {
            var result = Commands.Run("gh", args, project.Path, 20);
            if (result.Ok)
            {
                using var document = JsonDocument.Parse(result.Output);
                var entries = document.RootElement;
                var complete = entries.GetArrayLength() < limit;
                var matches = entries.EnumerateArray()
                    .Select(item => new OpenWorkCandidate
                    {
                        Number = item.GetProperty("number").GetInt32(),
                        Title = item.GetProperty("title").GetString() ?? "",
                        HeadBranch = pullRequests ? item.GetProperty("headRefName").GetString() ?? "" : "",
                        HeadCommit = pullRequests ? ReadPullRequestHead(item) : "",
                        Url = item.GetProperty("url").GetString() ?? ""
                    })
                    .Where(item => terms.Any(term => item.Title.Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                return (matches, complete, complete ? "" : label + " inventory reached its 100-item limit.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return ([], false, label + " inventory unavailable; inspect the hosting service before adoption.");
    }

    internal static (List<IssueObservation> Items, bool Complete, string Note) DiscoverIssues(ProjectInfo project, string repository)
    {
        const int limit = 100;
        try
        {
            var result = Commands.Run("gh", ["issue", "list", "--repo", "github.com/" + repository,
                "--state", "open", "--json", "number,title,url,updatedAt,author,state", "--limit", limit.ToString()],
                project.Path, 20);
            if (result.Ok)
            {
                using var document = JsonDocument.Parse(result.Output);
                var items = document.RootElement.EnumerateArray().Select(item => new IssueObservation(
                    item.GetProperty("number").GetInt32(), item.GetProperty("title").GetString() ?? "",
                    item.GetProperty("url").GetString() ?? "", item.GetProperty("updatedAt").GetString() ?? "",
                    item.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object &&
                    author.TryGetProperty("login", out var login) ? login.GetString() ?? "" : "",
                    item.GetProperty("state").GetString() ?? "",
                    author.ValueKind == JsonValueKind.Object && author.TryGetProperty("is_bot", out var bot) &&
                    bot.ValueKind == JsonValueKind.True)).ToList();
                var complete = items.Count < limit;
                return (items, complete, complete ? "" : "Open issue inventory reached its 100-item limit.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return ([], false, "Open issue inventory unavailable; inspect the hosting service before adoption.");
    }

    private static string ReadPullRequestHead(JsonElement item)
    {
        var commit = item.GetProperty("headRefOid").GetString() ?? "";
        if (!IntegrationVerifier.IsCommitSha(commit)) throw new JsonException("Missing or invalid pull request head");
        return commit.ToLowerInvariant();
    }

    public static WorkPackagePreflightReport? InspectDirectJob(
        FactoryState state, ProjectInfo project, string title, string description)
    {
        RequireMaster(project);
        // Old test fixtures may construct an incomplete ProjectInfo without a path.
        // Real registered projects always have a validated Git root.
        if (string.IsNullOrWhiteSpace(project.Path)) return null;
        var report = Inspect(state, project, new FactoryGoal
        {
            ProjectId = project.Id, Objective = title,
            AcceptanceCriteria = [description]
        });
        RequireCurrentMaster(report.RemoteMasterStatus);
        return report;
    }

    private static ExistingCodeCandidate? ParseMatch(string commit, string line)
    {
        // git grep -n for a tree-ish: <commit>:<path>:<line>:<snippet>
        var prefix = commit + ":";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = line[prefix.Length..];
        var pathEnd = rest.IndexOf(':');
        if (pathEnd <= 0) return null;
        var afterPath = rest[(pathEnd + 1)..];
        var lineEnd = afterPath.IndexOf(':');
        if (lineEnd <= 0 || !int.TryParse(afterPath[..lineEnd], out _)) return null;
        return new ExistingCodeCandidate
        {
            Path = rest[..pathEnd],
            Sample = afterPath[(lineEnd + 1)..][..Math.Min(160, afterPath.Length - lineEnd - 1)]
        };
    }

    private static List<string> Terms(FactoryGoal goal)
    {
        var words = Regex.Matches(goal.Objective + " " +
            string.Join(" ", goal.AcceptanceCriteria.Take(10)), "[\\p{L}\\p{Nd}_-]{5,}")
            .Select(m => m.Value.Trim('-','_'))
            .Where(w => w.Length >= 5 && w.Length <= 48 && !StopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        return words;
    }
}
