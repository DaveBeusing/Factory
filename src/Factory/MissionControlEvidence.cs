using System.Text.Json;

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

        var jobs = new List<object>();
        foreach (var job in state.Jobs.Where(j => j.ProjectId == projectId)
            .OrderByDescending(j => j.UpdatedAt).Take(100))
        {
            var cycles = new List<object>();
            using (var query = Command(db, tx, """
                SELECT cycle,generation,head_sha,report FROM review_cycles WHERE job_id=$job ORDER BY cycle;
                """, ("$job", job.Id)))
            using (var rows = query.ExecuteReader())
                while (rows.Read())
                {
                    using var report = rows.IsDBNull(3) ? null : JsonDocument.Parse(rows.GetString(3));
                    var root = report?.RootElement ?? default;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("quality", out var quality))
                        root = quality;
                    var reviews = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reviewRecords", out var records)
                        ? records.Deserialize<ReviewRecord[]>(Json.Options) ?? [] : [];
                    cycles.Add(new { cycle = rows.GetInt32(0), generation = rows.GetInt64(1),
                        head = rows.IsDBNull(2) ? null : rows.GetString(2), complete = report is not null, reviews });
                }
            var approved = false;
            try { approved = IsReviewApproved(db, tx, job, project); }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
            {
                // The worktree may have been removed since evidence was recorded.
            }
            ReviewApproval? approval = null;
            if (approved)
            {
                using var verified = Command(db, tx, """
                    SELECT payload FROM trusted_review_approvals WHERE job_id=$job ORDER BY cycle DESC LIMIT 1;
                    """, ("$job", job.Id));
                if (verified.ExecuteScalar() is string payload)
                    approval = JsonSerializer.Deserialize<ReviewApproval>(payload, Json.Options);
            }
            using var integration = Command(db, tx, """
                SELECT integrated_sha FROM scheduling_integrations WHERE job_id=$job AND project_id=$project AND head_sha=$head;
                """, ("$job", job.Id), ("$project", projectId), ("$head", job.HeadSha));
            var storedIntegration = integration.ExecuteScalar() as string;
            var integrated = state.VerifiedIntegrations.Contains(job.Id) ? storedIntegration : null;
            var historicalIntegration = state.HistoricalIntegrations.Contains(job.Id) ? storedIntegration : null;
            var remaining = Math.Max(0, Math.Clamp(job.MaxRounds, 1, ReviewPolicy.MaxCycles) - cycles.Count);
            var blockers = new List<string>();
            if (!string.IsNullOrWhiteSpace(job.Error)) blockers.Add(job.Error);
            if (integrated is null && !approved) blockers.Add("No current signed independent review.");
            if (integrated is null && remaining == 0 && !approved) blockers.Add("Review cycle limit reached.");
            using var published = Command(db, tx, """
                SELECT reference,resulting_state,at FROM lifecycle_audit WHERE entity_id=$job AND project_id=$project
                    AND entity_kind='publication-receipt' AND generation=$generation ORDER BY rowid DESC LIMIT 1;
                """, ("$job", job.Id), ("$project", projectId), ("$generation", job.ExecutionGeneration));
            string? url = null;
            string? historicalPrUrl = string.IsNullOrWhiteSpace(job.PrUrl) ? null : job.PrUrl;
            string? readinessBase = null;
            string? readinessObservedAt = null;
            string? mergedCommit = null;
            var ready = false;
            using (var row = published.ExecuteReader())
            if (row.Read())
            {
                using var doc = JsonDocument.Parse(row.GetString(0));
                if (doc.RootElement.GetProperty("headCommit").GetString() == job.HeadSha &&
                    doc.RootElement.TryGetProperty("projectRevision", out var revision) &&
                    revision.GetString() == ReviewPolicy.ProjectRevision(project))
                {
                    url = doc.RootElement.GetProperty("url").GetString();
                    if (url == historicalPrUrl) historicalPrUrl = null;
                    ready = row.GetString(1) == "ready" && approved &&
                        doc.RootElement.TryGetProperty("expiresAt", out var expires) && expires.GetDateTimeOffset() > DateTimeOffset.UtcNow;
                    if (row.GetString(1) == "ready")
                    {
                        readinessBase = doc.RootElement.GetProperty("baseCommit").GetString();
                        readinessObservedAt = row.GetString(2);
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
        var approvalQueue = state.Goals.Where(goal => goal.ProjectId == projectId && goal.State == "draft")
            .Select(goal => (goal, plan: ReadPlanRevision(db, tx, goal.Id)))
            .Where(item => item.plan?.Revision is not null)
            .Select(item => new { item.goal.Id, item.goal.Objective, item.plan!.Revision }).ToArray();
        return new { project.Id, intakePolicy = new { intakePolicy.Revision, intakePolicy.Rule },
            planPolicy = new { policy.Revision, rule = policy.Rule.ToString() }, intake, jobs, approvalQueue };
    }
}
