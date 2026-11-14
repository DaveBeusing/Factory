namespace Factory;

public static class SchedulingResources
{
    private static readonly string[] Kinds = ["interface", "schema", "ci", "config", "generated"];

    public static void Validate(IReadOnlyList<string> resources)
    {
        if (resources is null || resources.Count > 100 ||
            resources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != resources.Count ||
            resources.Any(resource => resource != "unknown" && !ValidKey(resource)))
            throw new ArgumentException("Use unique kind:name resources or unknown.");
    }

    private static bool ValidKey(string? value)
    {
        if (value is null) return false;
        var colon = value.IndexOf(':');
        return colon > 0 && colon < value.Length - 1 && value.Length <= 128 &&
            Kinds.Contains(value[..colon]) &&
            value[(colon + 1)..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/');
    }

    internal static bool Conflict(FactoryJob left, FactoryJob right)
    {
        try
        {
            Validate(left.SharedResources);
            Validate(right.SharedResources);
            return left.SharedResources.Contains("unknown") || right.SharedResources.Contains("unknown") ||
                left.SharedResources.Intersect(right.SharedResources, StringComparer.OrdinalIgnoreCase).Any();
        }
        catch (ArgumentException) { return true; }
    }
}

public sealed record SchedulingDecision(Guid JobId, string Reason, Guid? ConflictingJob = null);

public sealed partial class StateStore
{
    internal static void RecordIntegratedHead(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx, FactoryJob job, IntegrationProof proof) =>
        Execute(db, tx, """
            INSERT INTO scheduling_integrations(job_id,project_id,head_sha,integrated_sha)
            VALUES($job,$project,$head,$integrated)
            ON CONFLICT(job_id) DO UPDATE SET head_sha=excluded.head_sha,integrated_sha=excluded.integrated_sha;
            """, ("$job", job.Id), ("$project", job.ProjectId),
            ("$head", job.HeadSha), ("$integrated", proof.IntegratedCommit));
}
