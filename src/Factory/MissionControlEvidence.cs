using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Factory;

public sealed partial class StateStore
{
    private sealed record IntegrationTarget(string HeadSha, string IntegratedCommit, string Origin, string BaseBranch, string Path);

    internal static void RecordIntegrationTarget(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx, FactoryJob job, ProjectInfo project, IntegrationProof proof, FactoryState state) =>
        RecordLifecycle(db, tx, project.Id, "integration-target", job.Id, "integration-verifier",
            job.State, JobStates.Integrated, LifecycleReason.IntegrationVerified, reference: Json.Stringify(
                new IntegrationTarget(job.HeadSha, proof.IntegratedCommit, project.Origin, project.BaseBranch, project.Path)),
            generation: job.ExecutionGeneration, state: state);

    internal object ReadDecisionEvidence(Guid projectId, FactorySettings settings)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var state = Load(db, tx);
        var project = state.Projects.SingleOrDefault(p => p.Id == projectId)
            ?? throw new ArgumentException("Project no longer exists.");
        var policy = EffectivePlanPolicy(db, tx, projectId);
        var intakePolicy = ReadIntakePolicy(db, tx, projectId);
        var intake = new List<object>();
        using (var query = Command(db, tx, """
            SELECT d.repository,d.number,i.title,d.decision,d.reason,d.revision,d.policy_revision,d.at
            FROM issue_intake_decisions d JOIN issue_intake i USING(project_id,repository,number,revision)
            WHERE d.project_id=$project ORDER BY d.at DESC,d.rowid DESC LIMIT 100;
            """, ("$project", projectId)))
        using (var rows = query.ExecuteReader())
            while (rows.Read())
                intake.Add(new { repository = rows.GetString(0), number = rows.GetInt32(1), title = rows.GetString(2),
                    decision = rows.GetString(3), reason = rows.GetString(4), revision = rows.GetString(5),
                    policyRevision = rows.GetString(6), at = rows.GetString(7) });

        var selected = state.Jobs.Where(j => j.ProjectId == projectId).OrderByDescending(j => j.UpdatedAt).Take(100).ToArray();
        var evidence = ReadDecisionJobEvidence(db, tx, projectId, selected);
        var approvalQueue = state.Goals.Where(goal => goal.ProjectId == projectId && goal.State == "draft")
            .Select(goal => (goal, plan: ReadPlanRevision(db, tx, goal.Id)))
            .Where(item => item.plan?.Revision is not null)
            .Select(item => new { item.goal.Id, item.goal.Objective, item.plan!.Revision }).ToArray();
        tx.Commit();

        var projectRevision = ReviewPolicy.ProjectRevision(project);
        var jobs = new List<object>();
        foreach (var job in selected)
        {
            var details = evidence[job.Id];
            var cycles = details.Cycles.Select(FormatDecisionCycle).ToArray();
            ReviewApproval? approval = null;
            if (details.Approval is { } receipt)
            {
                try { approval = VerifyReviewApproval(job, projectRevision, receipt); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    // The worktree may have been removed since evidence was recorded.
                }
            }
            var approved = approval is not null;
            var integrated = state.VerifiedIntegrations.Contains(job.Id) ? details.IntegratedCommit : null;
            var historicalIntegration = state.HistoricalIntegrations.Contains(job.Id) ? details.IntegratedCommit : null;
            var remaining = Math.Max(0, Math.Clamp(job.MaxRounds, 1, ReviewPolicy.MaxCycles) - cycles.Length);
            var blockers = new List<string>();
            if (!string.IsNullOrWhiteSpace(job.Error)) blockers.Add(job.Error);
            if (integrated is null && !approved) blockers.Add("No current signed independent review.");
            if (integrated is null && remaining == 0 && !approved) blockers.Add("Review cycle limit reached.");
            string? url = null;
            string? historicalPrUrl = string.IsNullOrWhiteSpace(job.PrUrl) ? null : job.PrUrl;
            string? readinessBase = null;
            string? readinessObservedAt = null;
            string? mergedCommit = null;
            var ready = false;
            if (details.Publication is { } publication)
            {
                using var doc = JsonDocument.Parse(publication.Payload);
                if (doc.RootElement.GetProperty("headCommit").GetString() == job.HeadSha &&
                    doc.RootElement.TryGetProperty("projectRevision", out var revision) &&
                    revision.GetString() == projectRevision)
                {
                    url = doc.RootElement.GetProperty("url").GetString();
                    if (url == historicalPrUrl) historicalPrUrl = null;
                    ready = publication.State == "ready" && approved &&
                        doc.RootElement.TryGetProperty("expiresAt", out var expires) && expires.GetDateTimeOffset() > DateTimeOffset.UtcNow;
                    if (publication.State == "ready")
                    {
                        readinessBase = doc.RootElement.GetProperty("baseCommit").GetString();
                        readinessObservedAt = publication.At;
                        if (!ready) blockers.Add("PR readiness must be rechecked.");
                    }
                    if (doc.RootElement.TryGetProperty("mergeCommit", out var merged))
                        mergedCommit = merged.GetString();
                }
                else
                {
                    historicalPrUrl = doc.RootElement.GetProperty("url").GetString() ?? historicalPrUrl;
                    blockers.Add("The publication receipt is stale for this head or project policy.");
                }
            }
            if (integrated is null && !settings.AllowPush) blockers.Add("Publication is disabled in controller settings.");
            var status = integrated is not null ? "verified integrated" :
                mergedCommit is not null ? "merged" :
                ready ? "PR ready at last verification" :
                !string.IsNullOrWhiteSpace(url) ? "PR created" : "No confirmed PR";
            jobs.Add(new { job.Id, job.Title, job.State, job.HeadSha, prUrl = url, historicalPrUrl, readinessBase, readinessObservedAt,
                reviewApproved = approved, reviewer = approval?.Reviewer, reviewerSession = approval?.ReviewerSession,
                remaining, cycles, integrated, historicalIntegration, mergedCommit, status, blockers });
        }
        return new { project.Id, intakePolicy = new { intakePolicy.Revision, intakePolicy.Rule },
            planPolicy = new { policy.Revision, rule = policy.Rule.ToString() }, intake, jobs, approvalQueue };
    }

    private sealed record DecisionCycle(int Cycle, long Generation, string? Head, string? Report);
    private sealed record DecisionPublication(string Payload, string State, string At);

    private sealed class DecisionJobEvidence
    {
        internal List<DecisionCycle> Cycles { get; } = [];
        internal StoredReviewApproval? Approval { get; set; }
        internal DecisionPublication? Publication { get; set; }
        internal string? IntegratedCommit { get; set; }
    }

    private static Dictionary<Guid, DecisionJobEvidence> ReadDecisionJobEvidence(
        SqliteConnection db, SqliteTransaction tx, Guid projectId, FactoryJob[] selected)
    {
        var result = selected.ToDictionary(j => j.Id, _ => new DecisionJobEvidence());
        if (result.Count == 0) return result;
        var parameters = new (string, object?)[] { ("$project", projectId),
            ("$jobs", Json.Stringify(result.Keys.ToArray())) };
        using (var query = Command(db, tx, """
            SELECT c.job_id,c.cycle,c.generation,c.head_sha,c.report,a.payload,a.signature
            FROM review_cycles c
            JOIN jobs j ON j.id=c.job_id
            LEFT JOIN trusted_review_approvals a ON a.job_id=c.job_id AND a.cycle=c.cycle
                AND c.generation=j.execution_generation AND c.head_sha=j.head_sha
                AND c.cycle=(SELECT MAX(latest.cycle) FROM review_cycles latest WHERE latest.job_id=c.job_id)
            WHERE j.project_id=$project AND c.job_id IN (SELECT value FROM json_each($jobs))
            ORDER BY c.job_id,c.cycle;
            """, parameters))
        using (var rows = query.ExecuteReader())
            while (rows.Read())
            {
                var evidence = result[Guid.Parse(rows.GetString(0))];
                evidence.Cycles.Add(new(rows.GetInt32(1), rows.GetInt64(2), rows.IsDBNull(3) ? null : rows.GetString(3),
                    rows.IsDBNull(4) ? null : rows.GetString(4)));
                if (!rows.IsDBNull(5))
                    evidence.Approval = new(rows.GetString(5), rows.GetString(6));
            }
        using (var query = Command(db, tx, """
            SELECT j.id,i.integrated_sha,p.reference,p.resulting_state,p.at
            FROM jobs j
            LEFT JOIN scheduling_integrations i ON i.job_id=j.id AND i.project_id=$project AND i.head_sha=j.head_sha
            LEFT JOIN lifecycle_audit p ON p.rowid=(
                SELECT rowid FROM lifecycle_audit WHERE project_id=$project AND entity_kind='publication-receipt'
                    AND entity_id=j.id AND generation=j.execution_generation ORDER BY rowid DESC LIMIT 1)
            WHERE j.project_id=$project AND j.id IN (SELECT value FROM json_each($jobs));
            """, parameters))
        using (var rows = query.ExecuteReader())
            while (rows.Read())
            {
                var evidence = result[Guid.Parse(rows.GetString(0))];
                evidence.IntegratedCommit = rows.IsDBNull(1) ? null : rows.GetString(1);
                if (!rows.IsDBNull(2))
                    evidence.Publication = new(rows.GetString(2), rows.GetString(3), rows.GetString(4));
            }
        return result;
    }

    private static object FormatDecisionCycle(DecisionCycle cycle)
    {
        using var report = cycle.Report is null ? null : JsonDocument.Parse(cycle.Report);
        var root = report?.RootElement ?? default;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("quality", out var quality))
            root = quality;
        var reviews = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reviewRecords", out var records)
            ? records.Deserialize<ReviewRecord[]>(Json.Options) ?? [] : [];
        return new { cycle.Cycle, cycle.Generation, cycle.Head, complete = report is not null, reviews };
    }
}
