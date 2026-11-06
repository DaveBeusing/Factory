using System.Text.Json;

namespace Factory;

/// <summary>
/// Safe read-only projection of the controller's persisted quality evidence.
/// File paths are never supplied by the client and raw check/reviewer logs are
/// deliberately excluded from the browser response.
/// </summary>
public static class JobEvidenceReader
{
    public static object? Read(StateStore store, Guid jobId)
    {
        var job = store.Job(jobId);
        return Read(store, job.Id, job.HeadSha, job.EvidenceFile, includeDetails: true);
    }

    internal static object? ReadViewer(StateStore store, ViewerJobEvidence job) =>
        Read(store, job.Id, job.HeadSha, job.EvidenceFile, includeDetails: false);

    private static object? Read(StateStore store, Guid jobId, string headSha, string evidenceFile, bool includeDetails)
    {
        if (string.IsNullOrWhiteSpace(evidenceFile)) return null;

        var expectedDirectory = Path.GetFullPath(
            Path.Combine(store.Home, "evidence", jobId.ToString("N")));
        var path = Path.GetFullPath(evidenceFile);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(expectedDirectory) +
            Path.DirectorySeparatorChar, comparison) ||
            !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Job evidence path is outside its permitted directory");
        if (!File.Exists(path)) return null;

        var file = new FileInfo(path);
        if (file.Length > 2_000_000)
            throw new InvalidOperationException("Job evidence exceeds the inspection limit");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        if (!root.TryGetProperty("jobId", out var idElement) ||
            !Guid.TryParse(idElement.GetString(), out var evidenceJob) ||
            evidenceJob != jobId ||
            !root.TryGetProperty("commit", out var commitElement) ||
            commitElement.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Quality evidence identity mismatch");

        var commit = commitElement.GetString() ?? "";
        var current = !string.IsNullOrWhiteSpace(headSha) &&
            string.Equals(commit, headSha, StringComparison.OrdinalIgnoreCase);
        if (!root.TryGetProperty("report", out var reportElement) ||
            reportElement.ValueKind != JsonValueKind.Object)
            return includeDetails
                ? new { available = true, current, commit, status = "report_unavailable",
                checks = Array.Empty<object>(), reviews = Array.Empty<object>(),
                findings = Array.Empty<string>() }
                : new { available = true, current, status = "report_unavailable" };

        var report = reportElement.Deserialize<QualityResult>(Json.Options)
            ?? throw new InvalidOperationException("Invalid quality report");
        if (!string.Equals(report.HeadSha, commit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Quality report commit does not match evidence identity");

        var status = current ? (report.Passed ? "passed" : "changes_required") : "stale";
        if (!includeDetails) return new { available = true, current, status, verifiedAt = report.At };

        return new
        {
            available = true, current, commit, status,
            verifiedAt = report.At,
            checks = report.Checks.Select(c => new { c.Name, c.ExitCode,
                passed = c.ExitCode == 0 }).ToArray(),
            reviews = report.Reviews.Select(pair => new
            {
                role = pair.Key,
                verdict = pair.Value.StartsWith("PASS", StringComparison.Ordinal)
                    ? "PASS" : "CHANGES"
            }).ToArray(),
            findings = report.Findings.Select(f => f[..Math.Min(f.Length, 500)]).ToArray()
        };
    }
}
