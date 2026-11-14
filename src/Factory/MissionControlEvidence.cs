using System.Text.Json;

namespace Factory;

public sealed partial class StateStore
{
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
                    var reviews = report is not null && report.RootElement.TryGetProperty("reviewRecords", out var records)
                        ? records.Deserialize<ReviewRecord[]>(Json.Options) ?? [] : [];
                    cycles.Add(new { cycle = rows.GetInt32(0), generation = rows.GetInt64(1),
                        head = rows.IsDBNull(2) ? null : rows.GetString(2), complete = report is not null, reviews });
                }
            var approved = IsReviewApproved(db, tx, job, project);
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
            var integrated = state.VerifiedIntegrations.Contains(job.Id) ? integration.ExecuteScalar() as string : null;
            var remaining = Math.Max(0, Math.Clamp(job.MaxRounds, 1, ReviewPolicy.MaxCycles) - cycles.Count);
            var blockers = new List<string>();
            if (!string.IsNullOrWhiteSpace(job.Error)) blockers.Add(job.Error);
            if (integrated is null && !approved) blockers.Add("No current signed independent review.");
            if (integrated is null && remaining == 0 && !approved) blockers.Add("Review cycle limit reached.");
            using var published = Command(db, tx, """
                SELECT reference FROM lifecycle_audit WHERE entity_id=$job AND project_id=$project
                    AND entity_kind='publication-receipt' AND generation=$generation ORDER BY rowid DESC LIMIT 1;
                """, ("$job", job.Id), ("$project", projectId), ("$generation", job.ExecutionGeneration));
            var url = job.PrUrl;
            string? mergedCommit = null;
            if (published.ExecuteScalar() is string receipt)
            {
                using var doc = JsonDocument.Parse(receipt);
                if (doc.RootElement.GetProperty("headCommit").GetString() == job.HeadSha)
                {
                    url = doc.RootElement.GetProperty("url").GetString() ?? url;
                    if (doc.RootElement.TryGetProperty("mergeCommit", out var merged))
                        mergedCommit = merged.GetString();
                }
            }
            if (integrated is null && !settings.AllowPush) blockers.Add("Publication is disabled in controller settings.");
            var status = integrated is not null ? "verified integrated" :
                mergedCommit is not null ? "merged" :
                job.State == JobStates.PrReady && job.PrUrl.Length > 0 ? "PR ready" :
                url.Length > 0 ? "PR created" : "No confirmed PR";
            jobs.Add(new { job.Id, job.Title, job.State, job.HeadSha, prUrl = url,
                reviewApproved = approved, reviewer = approval?.Reviewer, reviewerSession = approval?.ReviewerSession,
                remaining, cycles, integrated, mergedCommit, status, blockers });
        }
        return new { project.Id, intakePolicy = new { intakePolicy.Revision, intakePolicy.Rule },
            planPolicy = new { policy.Revision, rule = policy.Rule.ToString() }, intake, jobs };
    }
}
