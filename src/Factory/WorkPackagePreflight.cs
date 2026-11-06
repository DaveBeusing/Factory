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
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> SearchTerms { get; set; } = [];
    public List<ExistingCodeCandidate> ExistingCode { get; set; } = [];
    public List<ExistingWorkCandidate> ExistingJobs { get; set; } = [];
    public List<OpenWorkCandidate> OpenPullRequests { get; set; } = [];
    public bool PullRequestsChecked { get; set; }
    public string PullRequestCheckNote { get; set; } = "";
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
        if (!Regex.IsMatch(commit, "^[0-9a-fA-F]{40,64}$"))
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

        // GitHub PR inspection is optional in local-first deployments. Unknown is
        // explicitly recorded; a missing gh CLI or authentication is not 'no open PR'.
        var origin = GitOperations.Origin(project.Path) ?? "";
        if (!origin.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            report.PullRequestCheckNote = "GitHub PR inventory not applicable or origin unavailable.";
        }
        else
        {
            try
            {
                var result = Commands.Run("gh",
                    ["pr", "list", "--state", "open", "--base", "master",
                     "--json", "number,title,headRefName,url", "--limit", "100"],
                    project.Path, 20);
                if (!result.Ok)
                    report.PullRequestCheckNote = "Open PR inventory unavailable; check the hosting service before adoption.";
                else
                {
                    using var document = JsonDocument.Parse(result.Output);
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        throw new JsonException("Unexpected pull-request response");
                    report.PullRequestsChecked = true;
                    report.OpenPullRequests = document.RootElement.EnumerateArray()
                        .Select(pr => new OpenWorkCandidate
                        {
                            Number = pr.GetProperty("number").GetInt32(),
                            Title = pr.GetProperty("title").GetString() ?? "",
                            HeadBranch = pr.GetProperty("headRefName").GetString() ?? "",
                            Url = pr.GetProperty("url").GetString() ?? ""
                        })
                        .Where(pr => terms.Any(term =>
                            pr.Title.Contains(term, StringComparison.OrdinalIgnoreCase)))
                        .Take(30).ToList();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.PullRequestCheckNote = "Open PR inventory unavailable; inspect open PRs manually.";
            }
        }
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
