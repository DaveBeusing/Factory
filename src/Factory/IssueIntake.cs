using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Factory;

internal sealed record IssueObservation(int Number, string Title, string Url, string UpdatedAt, string Author, string State,
    bool Automated = false);
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
                project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
                revision TEXT NOT NULL, rule TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS issue_intake_goals(
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                repository TEXT NOT NULL, number INTEGER NOT NULL, goal_id TEXT NOT NULL,
                PRIMARY KEY(project_id,repository,number)
            );
            PRAGMA user_version=21;
            """);

    private static readonly Guid IntakeBaseline = Guid.Parse("00000000-0000-0000-0000-000000000069");

    private static (Guid Revision, string Rule) ReadIntakePolicy(SqliteConnection db, SqliteTransaction tx, Guid project)
    {
        using var query = Command(db, tx, "SELECT revision,rule FROM issue_intake_policies WHERE project_id=$project;",
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
            INSERT INTO issue_intake_policies(project_id,revision,rule) VALUES($project,$revision,$rule)
            ON CONFLICT(project_id) DO UPDATE SET revision=excluded.revision,rule=excluded.rule;
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
        var (items, complete, note) = WorkPackagePreflight.DiscoverIssues(project, repository);
        return PersistIssueScan(project, repository, items, complete, note);
    }

    internal IReadOnlyList<IssueIntakeResult> PersistIssueScan(ProjectInfo project, string repository,
        IReadOnlyList<IssueObservation> items, bool complete, string note)
    {
        var projectId = project.Id;
        var configured = WorkPackagePreflight.GitHubRepository(project.Origin);
        if (!string.Equals(configured, repository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Issue inventory does not belong to the project repository.");
        repository = repository.ToLowerInvariant();
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
        if (!complete && items.Count == 0)
            throw new InvalidOperationException(note);
        var policy = ReadIntakePolicy(db, tx, projectId);
        var results = items.Distinct().Select(item => RecordIssueObservation(db, tx, projectId, repository, policy, item, complete)).ToArray();
        tx.Commit();
        return results;
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
            policy.Rule switch
            {
                "accept" => ("accept", "project_policy_accept"),
                "deny" => ("deny", "project_policy_denied"),
                "escalate" => ("escalate", "project_policy_escalate"),
                _ => ("hold", "intake_acceptance_not_authorized")
            };
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
