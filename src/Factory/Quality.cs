using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Factory;

public sealed class QualityResult
{
    public bool Passed { get; set; }
    public string HeadSha { get; set; } = "";
    public List<CheckReport> Checks { get; set; } = [];
    public List<string> Findings { get; set; } = [];
    public Dictionary<string, string> Reviews { get; set; } = [];
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CheckReport
{
    public string Name { get; set; } = "";
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class QualityEngine
{
    private readonly IAgentRuntime _agent;
    private readonly Func<string, IAgentRuntime>? _reviewRuntime;
    public QualityEngine(IAgentRuntime agent, Func<string, IAgentRuntime>? reviewRuntime = null)
    {
        _agent = agent;
        _reviewRuntime = reviewRuntime;
    }
    public async Task<QualityResult> GateAsync(ProjectInfo project, FactoryJob job, CancellationToken ct)
    {
        var quality = new QualityResult { HeadSha = GitOperations.Head(job.Worktree) };
        if (project.Checks.Count == 0)
        {
            quality.Findings.Add("No executable project checks configured");
           return quality;
        }
        IReadOnlyList<string> changed;
        try
        {
            changed = WorkPackageScope.ValidateWorktree(project, job);
        }
        catch (ScopeViolationException ex)
        {
            quality.Findings.Add(ex.Message);
            return quality;
        }
        catch (InvalidOperationException ex)
        {
            quality.Findings.Add(ex.Message);
            return quality;
        }
        foreach (var file in changed)
        {
            var absolute = Path.GetFullPath(Path.Combine(job.Worktree, file));
           var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(job.Worktree)) + Path.DirectorySeparatorChar;
           if (!absolute.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                quality.Findings.Add("Changed path escapes worktree: " + file); continue;
           }
            if (!File.Exists(absolute) || new FileInfo(absolute).Length > 2_000_000) continue;
           var text = await File.ReadAllTextAsync(absolute, ct);
           // Heuristic; not a replacement for dedicated secret scanning.
            if (Regex.IsMatch(text, @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|(?i:password\s*=\s*['""].{8,}['""])", RegexOptions.None))
                quality.Findings.Add("Possible embedded secret in " + file);
        }
        foreach (var check in project.Checks)
        {
            if (string.IsNullOrWhiteSpace(check.Executable)) { quality.Findings.Add("Empty check executable"); continue; }
            try
            {
                var isolated = !string.IsNullOrWhiteSpace(check.ContainerImage);
               var executable = isolated ? "docker" : check.Executable;
               var arguments = isolated
                    ? new List<string> { "run", "--rm", "--network", "none", "--read-only", "--tmpfs", "/tmp:rw,noexec,nosuid",
                        "--mount", "type=bind,source=" + job.Worktree + ",target=/work,readonly", "--workdir", "/work",
                        check.ContainerImage, check.Executable }.Concat(check.Arguments).ToList()
                    : check.Arguments;
               var response = await Commands.RunAsync(executable, arguments,
                    job.Worktree, check.TimeoutSeconds, cancellationToken: ct);
               quality.Checks.Add(new CheckReport
                {
                    Name = check.Name, ExitCode = response.Code,
                    Output = response.Output[^Math.Min(4000, response.Output.Length)..],
                    Error = response.Error[^Math.Min(4000, response.Error.Length)..]
                });
               if (!response.Ok) quality.Findings.Add("Check failed: " + check.Name);
           }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                quality.Findings.Add("Check exception: " + check.Name + ": " + ex.Message);
           }
        }
        quality.Passed = quality.Findings.Count == 0 && quality.Checks.Count == project.Checks.Count;
        return quality;
    }
    public async Task<QualityResult> ReviewAsync(ProjectInfo project, FactoryJob job, QualityResult report,
        CancellationToken ct)
    {
        if (!report.Passed) return report;
        var reviewers = new Dictionary<string, string>
        {
            ["code"] = "Correctness, tests, edge cases, maintainability",
            ["architecture"] = "Dependencies, modularity, interface and scope violations",
            ["security"] = "Secrets, untrusted input, access control, unsafe execution",
            ["ux"] = "User-visible behavior, discoverability and usability when applicable"
        };
        var diff = Commands.Run("git", ["-C", job.Worktree, "diff", $"{project.BaseBranch}...HEAD", "--stat"],
            Environment.CurrentDirectory);
        var completed = await Task.WhenAll(reviewers.Select(async pair =>
        {
            var (role, checklist) = pair;
           var instruction = "Review only; DO NOT edit files, commit, push or merge. " + checklist +
                ". Review this branch against " + project.BaseBranch + ". Return first line exactly VERDICT: PASS or VERDICT: CHANGES. " +
                "Second line onward provide evidence with file paths. Task: " + job.Description + "\nDiff summary:\n" + diff.Output;
           try
            {
                var result = await (_reviewRuntime?.Invoke(role) ?? _agent)
                    .RunAsync(job.Worktree, instruction, "", ct, readOnly: true);
               var verdict = result.Success && result.FinalText.TrimStart().StartsWith("VERDICT: PASS", StringComparison.Ordinal)
                    ? "PASS" : "CHANGES";
               return (role, verdict, result.FinalText[..Math.Min(3000, result.FinalText.Length)]);
           }
            catch (Exception ex) when (ex is not OperationCanceledException and not TokenBudgetExceededException)
            {
                return (role, "CHANGES", "Reviewer exception: " + ex.Message);
           }
        }));
        foreach (var (role, verdict, message) in completed)
        {
            report.Reviews[role] = verdict + "\n" + message;
           if (verdict != "PASS") report.Findings.Add("Review requested changes: " + role);
        }
        report.Passed = report.Findings.Count == 0 && report.Reviews.Values.All(v => v.StartsWith("PASS", StringComparison.Ordinal));
        return report;
    }
    public static string Evidence(StateStore store, FactoryJob job, QualityResult report)
    {
        var dir = Path.Combine(store.Home, "evidence", job.Id.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, report.HeadSha + ".json");
        File.WriteAllText(path, Json.Stringify(new
        {
            jobId = job.Id, commit = report.HeadSha, report, job.CreatedAt, job.Attempt,
            outcome = report.Passed ? "ready" : "changes"
        }));
        return path;
    }
}
