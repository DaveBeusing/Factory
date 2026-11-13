using Microsoft.Data.Sqlite;

namespace Factory;

internal enum PolicyOutcome { Allow, Deny, Hold }
internal enum PolicyRisk { Low, Medium, High }
internal enum AdoptionRule { Manual, IndependentApproval, Deny }

internal sealed record PolicyContext(string Actor, Guid Project, Guid Source, string Action,
    PolicyRisk Risk, bool HasCapability, bool Manual);
internal sealed record PolicyVersion(Guid Revision, AdoptionRule Rule);
internal sealed record PolicyApproval(string Actor, Guid Project, Guid Source, Guid PolicyRevision,
    DateTimeOffset ExpiresAt, bool GrantCurrent, bool Revoked);
public sealed record PlanPolicyInput(string Rule, Guid ExpectedRevision);
public sealed record PlanApprovalInput(Guid SourceRevision, Guid PolicyRevision, int Minutes, bool Revoke = false);

internal static class FactoryPolicy
{
    internal const string AdoptPlan = "plan.adopt.v1";
    internal static readonly PolicyVersion Baseline = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"), AdoptionRule.Manual);

    internal static (PolicyOutcome Outcome, string Reason) Decide(PolicyContext request, PolicyVersion policy,
        PolicyApproval? approval, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(request.Actor) || request.Project == Guid.Empty ||
            request.Source == Guid.Empty || !request.HasCapability ||
            request.Action != AdoptPlan || !Enum.IsDefined(request.Risk) || !request.Manual ||
            policy.Revision == Guid.Empty || !Enum.IsDefined(policy.Rule))
            return (PolicyOutcome.Deny, "unsupported_or_unauthorized");
        if (policy.Rule == AdoptionRule.Deny)
            return (PolicyOutcome.Deny, "project_policy_denied");
        if (policy.Rule == AdoptionRule.Manual || request.Risk != PolicyRisk.High)
            return (PolicyOutcome.Allow, "manual_review");
        if (approval is null)
            return (PolicyOutcome.Hold, "independent_approval_required");
        if (string.IsNullOrWhiteSpace(approval.Actor) ||
            string.Equals(approval.Actor, request.Actor, StringComparison.OrdinalIgnoreCase) ||
            !approval.GrantCurrent || approval.Revoked || approval.ExpiresAt <= now ||
            approval.Project != request.Project || approval.Source != request.Source ||
            approval.PolicyRevision != policy.Revision)
            return (PolicyOutcome.Deny, "invalid_approval");
        return (PolicyOutcome.Allow, "independent_approval");
    }
}

public sealed partial class StateStore
{
    private static void UpgradeFactoryPolicy(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS factory_policy_receipts(
                id TEXT PRIMARY KEY NOT NULL, project_id TEXT NOT NULL, source_revision TEXT NOT NULL,
                policy_revision TEXT NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL,
                risk TEXT NOT NULL, outcome TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS factory_project_policies(
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                version INTEGER NOT NULL, revision TEXT NOT NULL UNIQUE, rule TEXT NOT NULL,
                PRIMARY KEY(project_id,version)
            );
            CREATE TABLE IF NOT EXISTS factory_plan_approvals(
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                source_revision TEXT NOT NULL, policy_revision TEXT NOT NULL, actor TEXT NOT NULL,
                grant_revision INTEGER NOT NULL, expires_at TEXT NOT NULL, revoked INTEGER NOT NULL,
                PRIMARY KEY(project_id,source_revision,policy_revision,actor)
            );
            PRAGMA user_version=20;
            """);

    private static PolicyVersion EffectivePlanPolicy(SqliteConnection db, SqliteTransaction tx, Guid project)
    {
        using var query = Command(db, tx, """
            SELECT revision,rule FROM factory_project_policies WHERE project_id=$project ORDER BY version DESC LIMIT 1;
            """, ("$project", project));
        using var row = query.ExecuteReader();
        if (!row.Read()) return FactoryPolicy.Baseline;
        return new(Guid.Parse(row.GetString(0)), Enum.Parse<AdoptionRule>(row.GetString(1)));
    }

    internal Guid SetPlanPolicy(Guid project, PlanPolicyInput input)
    {
        if (!Enum.TryParse<AdoptionRule>(input.Rule, ignoreCase: false, out var rule) ||
            !Enum.IsDefined(rule) || input.Rule != rule.ToString())
            throw new ArgumentException("Choose Manual, IndependentApproval or Deny.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (EffectivePlanPolicy(db, tx, project).Revision != input.ExpectedRevision)
            throw new InvalidOperationException("The policy changed. Reload before updating.");
        var revision = Guid.NewGuid();
        Execute(db, tx, """
            INSERT INTO factory_project_policies(project_id,version,revision,rule)
            SELECT $project,COALESCE(MAX(version),0)+1,$revision,$rule
            FROM factory_project_policies WHERE project_id=$project;
            """, ("$project", project), ("$revision", revision), ("$rule", rule.ToString()));
        tx.Commit();
        return revision;
    }

    internal Guid PlanPolicyRevision(Guid project)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        return EffectivePlanPolicy(db, tx, project).Revision;
    }

    internal void SetPlanApproval(string authorization, Guid project, Guid goal, PlanApprovalInput input)
    {
        var verifier = RequirePlanVerifier(authorization);
        if (!input.Revoke && input.Minutes is < 1 or > 1440)
            throw new ArgumentException("Approval lifetime must be 1 to 1440 minutes.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        var scope = ReadPlanScope(db, tx, verifier, project, goal);
        RequireDraftPlanTarget(scope);
        var grant = ReadPlannerGrantRevision(db, tx, scope.SubjectId, project);
        var policy = EffectivePlanPolicy(db, tx, project);
        if (scope.StoredPlan?.Revision != input.SourceRevision || input.SourceRevision == Guid.Empty ||
            policy.Revision != input.PolicyRevision || policy.Rule != AdoptionRule.IndependentApproval || grant is null)
            throw new ScopedProjectRejected(DraftGoalStatus.Conflict, "The plan, grant or policy changed.");
        Execute(db, tx, """
            INSERT INTO factory_plan_approvals(project_id,source_revision,policy_revision,actor,grant_revision,expires_at,revoked)
            VALUES($project,$source,$policy,$actor,$grant,$expires,$revoked)
            ON CONFLICT(project_id,source_revision,policy_revision,actor) DO UPDATE SET
                grant_revision=excluded.grant_revision,expires_at=excluded.expires_at,revoked=excluded.revoked;
            """, ("$project", project), ("$source", input.SourceRevision), ("$policy", policy.Revision),
            ("$actor", scope.SubjectId), ("$grant", grant), ("$revoked", input.Revoke),
            ("$expires", DateTimeOffset.UtcNow.AddMinutes(input.Revoke ? 0 : input.Minutes)));
        tx.Commit();
    }

    private static List<PolicyApproval> ReadPlanApprovals(SqliteConnection db, SqliteTransaction tx, PolicyContext context,
        PolicyVersion policy)
    {
        using var query = Command(db, tx, """
            SELECT a.actor,a.expires_at,a.revoked, EXISTS(
                SELECT 1 FROM planner_project_grants p JOIN viewer_subjects v ON v.id=p.subject_id
                WHERE p.project_id=a.project_id AND p.subject_id=a.actor AND v.verifier IS NOT NULL AND
                  a.grant_revision=(SELECT MAX(id) FROM viewer_access_audit
                    WHERE project_id=a.project_id AND subject_id=a.actor AND operation='grant_planner')
            )
            FROM factory_plan_approvals a WHERE a.project_id=$project AND a.source_revision=$source
                AND a.policy_revision=$policy ORDER BY a.actor;
            """, ("$project", context.Project), ("$source", context.Source), ("$policy", policy.Revision));
        using var row = query.ExecuteReader();
        var approvals = new List<PolicyApproval>();
        while (row.Read())
            approvals.Add(new(row.GetString(0), context.Project, context.Source, policy.Revision,
                (DateTimeOffset)FromDb(row.GetValue(1), typeof(DateTimeOffset))!, row.GetBoolean(3), row.GetBoolean(2)));
        return approvals;
    }

    internal static PolicyOutcome DecidePlanAdoption(SqliteConnection db, SqliteTransaction tx,
        FactoryPlan plan, string actor)
    {
        var risk = plan.Packages.Any(p => p.Risk == "high") ? PolicyRisk.High :
            plan.Packages.Any(p => p.Risk == "medium") ? PolicyRisk.Medium : PolicyRisk.Low;
        var context = new PolicyContext(actor, plan.ProjectId, plan.Revision ?? Guid.Empty,
            FactoryPolicy.AdoptPlan, risk, HasCapability: true, Manual: true);
        var policy = EffectivePlanPolicy(db, tx, plan.ProjectId);
        var now = DateTimeOffset.UtcNow;
        var (outcome, reason) = FactoryPolicy.Decide(context, policy, null, now);
        if (outcome == PolicyOutcome.Hold)
        {
            foreach (var approval in ReadPlanApprovals(db, tx, context, policy))
            {
                (outcome, reason) = FactoryPolicy.Decide(context, policy, approval, now);
                if (outcome == PolicyOutcome.Allow) break;
            }
        }
        Execute(db, tx, """
            INSERT INTO factory_policy_receipts(id,project_id,source_revision,policy_revision,actor,action,risk,outcome,reason,at)
            VALUES($id,$project,$source,$policy,$actor,$action,$risk,$outcome,$reason,$at);
            """, ("$id", Guid.NewGuid()), ("$project", context.Project), ("$source", context.Source),
            ("$policy", policy.Revision), ("$actor", context.Actor), ("$action", context.Action),
            ("$risk", risk.ToString()), ("$outcome", outcome.ToString()), ("$reason", reason), ("$at", now));
        return outcome;
    }
}
