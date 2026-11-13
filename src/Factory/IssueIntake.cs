using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Factory;

internal sealed record IssueObservation(int Number, string Title, string Url, string UpdatedAt, string Author, string State,
    bool Automated = false, string ContentHash = "");
internal sealed record IssueIntakeResult(int Number, string Revision, string Decision, string Reason, Guid? GoalId);
public sealed record IssueIntakePolicyInput(string Rule, Guid ExpectedRevision);

public sealed partial class StateStore
{
    private static void UpgradeIssueIntake(SqliteConnection db, SqliteTransaction tx) =>
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS issue_intake(
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                repository TEXT NOT NULL, number INTEGER NOT NULL, revision TEXT NOT NULL,
                title TEXT NOT NULL, author TEXT NOT NULL, source_state TEXT NOT NULL, source_updated_at TEXT NOT NULL,
                PRIMARY KEY(project_id,repository,number,revision)
            );
            CREATE TABLE IF NOT EXISTS issue_intake_decisions(
                id TEXT PRIMARY KEY, project_id TEXT NOT NULL, repository TEXT NOT NULL,
                number INTEGER NOT NULL, revision TEXT NOT NULL, policy_revision TEXT NOT NULL,
                decision TEXT NOT NULL, reason TEXT NOT NULL, actor TEXT NOT NULL, at TEXT NOT NULL,
                UNIQUE(project_id,repository,number,revision,policy_revision,decision,reason),
                FOREIGN KEY(project_id,repository,number,revision) REFERENCES issue_intake ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS issue_intake_policies(
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                version INTEGER NOT NULL, revision TEXT NOT NULL UNIQUE, rule TEXT NOT NULL,
                PRIMARY KEY(project_id,version)
            );
            CREATE TABLE IF NOT EXISTS issue_intake_goals(
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                repository TEXT NOT NULL, number INTEGER NOT NULL, goal_id TEXT NOT NULL,
                policy_revision TEXT, source_revision TEXT,
                PRIMARY KEY(project_id,repository,number)
            );
            CREATE TABLE IF NOT EXISTS issue_intake_plan_bindings(
                plan_revision TEXT PRIMARY KEY, policy_revision TEXT NOT NULL, source_revision TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS issue_intake_scans(
                project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
                generation INTEGER NOT NULL
            );
            PRAGMA user_version=21;
            """);

    private static readonly Guid IntakeBaseline = Guid.Parse("00000000-0000-0000-0000-000000000069");

    private static (Guid Revision, string Rule) ReadIntakePolicy(SqliteConnection db, SqliteTransaction tx, Guid project)
    {
        using var query = Command(db, tx, "SELECT revision,rule FROM issue_intake_policies WHERE project_id=$project ORDER BY version DESC LIMIT 1;",
            ("$project", project));
        using var row = query.ExecuteReader();
        return row.Read() ? (Guid.Parse(row.GetString(0)), row.GetString(1)) : (IntakeBaseline, "hold");
    }

    internal object IssueIntakePolicy(Guid project)
    {
        _ = Project(project.ToString());
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var (revision, rule) = ReadIntakePolicy(db, tx, project);
        return new { revision, rule };
    }

    internal Guid SetIssueIntakePolicy(Guid project, IssueIntakePolicyInput input)
    {
        if (input.Rule is not ("hold" or "deny" or "accept" or "escalate"))
            throw new ArgumentException("Choose hold, deny, accept or escalate.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (ReadIntakePolicy(db, tx, project).Revision != input.ExpectedRevision)
            throw new InvalidOperationException("The intake policy changed. Reload before updating.");
        var revision = Guid.NewGuid();
        Execute(db, tx, """
            INSERT INTO issue_intake_policies(project_id,version,revision,rule)
            SELECT $project,COALESCE(MAX(version),0)+1,$revision,$rule FROM issue_intake_policies WHERE project_id=$project;
            """, ("$project", project), ("$revision", revision), ("$rule", input.Rule));
        tx.Commit();
        return revision;
    }

    internal IReadOnlyList<IssueIntakeResult> ScanIssues(Guid projectId)
    {
        var project = Read().Projects.Single(p => p.Id == projectId);
        var repository = WorkPackagePreflight.GitHubRepository(project.Origin)
            ?? throw new InvalidOperationException("The project has no configured GitHub origin.");
        WorkPackagePreflight.RequireMaster(project);
        var generation = BeginIssueScan(projectId);
        var (items, complete, note) = WorkPackagePreflight.DiscoverIssues(project, repository, paginated: true);
        return PersistIssueScan(project, repository, items, complete, note, generation);
    }

    internal long BeginIssueScan(Guid projectId)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var reserve = Command(db, tx, """
            INSERT INTO issue_intake_scans(project_id,generation) VALUES($project,1)
            ON CONFLICT(project_id) DO UPDATE SET generation=generation+1 RETURNING generation;
            """, ("$project", projectId));
        var generation = (long)reserve.ExecuteScalar()!;
        Execute(db, tx, """
            UPDATE issue_intake_goals SET policy_revision=NULL,source_revision=NULL WHERE project_id=$project;
            """, ("$project", projectId));
        tx.Commit();
        return generation;
    }

    internal IReadOnlyList<IssueIntakeResult> PersistIssueScan(ProjectInfo project, string repository,
        IReadOnlyList<IssueObservation> items, bool complete, string note, long? generation = null)
    {
        var projectId = project.Id;
        var configured = WorkPackagePreflight.GitHubRepository(project.Origin);
        if (!string.Equals(configured, repository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Issue inventory does not belong to the project repository.");
        repository = repository.ToLowerInvariant();
        var expected = generation ?? BeginIssueScan(projectId);
        if (items.GroupBy(item => item.Number).Any(group => group.Distinct().Count() > 1))
            complete = false;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var query = Command(db, tx, "SELECT origin,path,status,base_branch FROM projects WHERE id=$id;", ("$id", projectId)))
        using (var row = query.ExecuteReader())
        {
            if (!row.Read() || row.GetString(0) != project.Origin || row.GetString(1) != project.Path ||
                row.GetString(2) != "active" || row.GetString(3) != project.BaseBranch)
                throw new InvalidOperationException("The project is inactive or its checkout configuration changed.");
        }
        using (var latest = Command(db, tx, """
            SELECT generation FROM issue_intake_scans WHERE project_id=$project;
            """, ("$project", projectId)))
        {
            if ((long?)latest.ExecuteScalar() != expected)
                throw new InvalidOperationException("A newer issue scan superseded this inventory.");
        }
        // Invalidate previous acceptance before consuming this observation of the inventory.
        Execute(db, tx, """
            UPDATE issue_intake_goals SET policy_revision=NULL,source_revision=NULL
            WHERE project_id=$project AND repository=$repo;
            """, ("$project", projectId), ("$repo", repository));
        if (!complete && items.Count == 0)
        {
            tx.Commit();
            throw new InvalidOperationException(note);
        }
        var policy = ReadIntakePolicy(db, tx, projectId);
        var results = items.Distinct().Select(item => RecordIssueObservation(db, tx, projectId, repository, policy, item, complete)).ToArray();
        tx.Commit();
        return results;
    }

    internal static bool IsIntakeGoal(SqliteConnection db, SqliteTransaction tx, Guid goal)
    {
        using var query = Command(db, tx, "SELECT 1 FROM issue_intake_goals WHERE goal_id=$goal LIMIT 1;",
            ("$goal", goal));
        return query.ExecuteScalar() is not null;
    }

    private static void BindIntakePlan(SqliteConnection db, SqliteTransaction tx, FactoryPlan plan) =>
        Execute(db, tx, """
            INSERT INTO issue_intake_plan_bindings(plan_revision,policy_revision,source_revision)
            SELECT $plan,policy_revision,source_revision FROM issue_intake_goals
            WHERE project_id=$project AND goal_id=$goal AND policy_revision IS NOT NULL AND source_revision IS NOT NULL;
            """, ("$plan", plan.Revision), ("$project", plan.ProjectId), ("$goal", plan.GoalId));

    internal static bool IntakeAllowsPlan(SqliteConnection db, SqliteTransaction tx, FactoryPlan plan)
    {
        using var query = Command(db, tx, """
            SELECT p.origin,g.repository,g.policy_revision,
                EXISTS(SELECT 1 FROM issue_intake_plan_bindings b WHERE b.plan_revision=$plan
                    AND b.policy_revision=g.policy_revision AND b.source_revision=g.source_revision)
            FROM issue_intake_goals g JOIN projects p ON p.id=g.project_id
            WHERE g.project_id=$project AND g.goal_id=$goal;
            """, ("$project", plan.ProjectId), ("$goal", plan.GoalId), ("$plan", plan.Revision));
        using var row = query.ExecuteReader();
        if (!row.Read()) return true;
        var origin = WorkPackagePreflight.GitHubRepository(row.GetString(0));
        var matches = string.Equals(origin, row.GetString(1), StringComparison.OrdinalIgnoreCase);
        var policyRevision = row.IsDBNull(2) ? null : row.GetString(2);
        var observed = row.GetBoolean(3);
        row.Close();
        var policy = ReadIntakePolicy(db, tx, plan.ProjectId);
        return matches && observed && policy.Rule == "accept" && policy.Revision.ToString() == policyRevision;
    }

    private static IssueIntakeResult RecordIssueObservation(SqliteConnection db, SqliteTransaction tx,
        Guid projectId, string repository, (Guid Revision, string Rule) policy, IssueObservation item, bool complete)
    {
        if (item.Number <= 0 || !Uri.TryCreate(item.Url, UriKind.Absolute, out var url) ||
            url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
            !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !url.AbsolutePath.Equals($"/{repository}/issues/{item.Number}", StringComparison.OrdinalIgnoreCase) ||
            !DateTimeOffset.TryParse(item.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var updated))
            throw new InvalidOperationException("GitHub returned an invalid or out-of-project issue identity.");

        item = item with { UpdatedAt = updated.UtcDateTime.ToString("O") };
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Stringify(item))));
        using var newer = Command(db, tx, """
            SELECT 1 FROM issue_intake WHERE project_id=$project AND repository=$repo AND number=$number
              AND (source_updated_at>$updated OR (source_updated_at=$updated AND revision<>$revision)) LIMIT 1;
            """, ("$project", projectId), ("$repo", repository), ("$number", item.Number),
            ("$updated", item.UpdatedAt), ("$revision", revision));
        var stale = newer.ExecuteScalar() is not null;
        var (decision, reason) = stale ? ("hold", "stale_or_ambiguous_source") :
            !complete ? ("hold", "inventory_incomplete") :
            item.State != "OPEN" ? ("deny", "issue_not_open") :
            item.Automated || item.Author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ||
                item.Author.Length == 0 ? ("hold", "automated_or_unknown_author") :
            FactoryPolicy.DecideIssueIntake(projectId, policy.Revision, policy.Rule);
        Guid? goalId = null;
        if (decision == "accept")
        {
            using var query = Command(db, tx, """
                SELECT goal_id FROM issue_intake_goals WHERE project_id=$project AND repository=$repo AND number=$number;
                """, ("$project", projectId), ("$repo", repository), ("$number", item.Number));
            if (query.ExecuteScalar() is string existing)
                goalId = Guid.Parse(existing);
            else
            {
                var goal = new FactoryGoal { ProjectId = projectId,
                    Objective = $"Review GitHub issue {repository}#{item.Number}", State = "draft" };
                using var next = Command(db, tx, "SELECT COALESCE(MAX(position),-1)+1 FROM goals;");
                Upsert(db, tx, Entities.Single(map => map.Type == typeof(FactoryGoal)), goal,
                    Convert.ToInt32(next.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
                Execute(db, tx, """
                    INSERT INTO issue_intake_goals(project_id,repository,number,goal_id)
                    VALUES($project,$repo,$number,$goal);
                    """, ("$project", projectId), ("$repo", repository), ("$number", item.Number), ("$goal", goal.Id));
                goalId = goal.Id;
            }
            Execute(db, tx, """
                UPDATE issue_intake_goals SET policy_revision=$policy,source_revision=$source
                WHERE project_id=$project AND repository=$repo AND number=$number;
                """, ("$policy", policy.Revision), ("$source", revision),
                ("$project", projectId), ("$repo", repository), ("$number", item.Number));
        }
        Execute(db, tx, """
            INSERT INTO issue_intake(project_id,repository,number,revision,title,author,source_state,source_updated_at)
            VALUES($project,$repo,$number,$revision,$title,$author,$state,$updated)
            ON CONFLICT DO NOTHING;
            """, ("$project", projectId), ("$repo", repository), ("$number", item.Number), ("$revision", revision),
            ("$title", item.Title), ("$author", item.Author), ("$state", item.State), ("$updated", item.UpdatedAt));
        Execute(db, tx, """
            INSERT INTO issue_intake_decisions(id,project_id,repository,number,revision,policy_revision,decision,reason,actor,at)
            VALUES($id,$project,$repo,$number,$revision,$policy,$decision,$reason,'local-operator',$at)
            ON CONFLICT DO NOTHING;
            """, ("$id", Guid.NewGuid()), ("$project", projectId), ("$repo", repository), ("$number", item.Number),
            ("$revision", revision), ("$policy", policy.Revision), ("$decision", decision), ("$reason", reason),
            ("$at", DateTimeOffset.UtcNow));
        return new(item.Number, revision, decision, reason, goalId);
    }
}
