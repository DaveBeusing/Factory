using Microsoft.Data.Sqlite;

namespace Factory;

internal sealed record ProjectActivityEntry(Guid Id, Guid JobId, string JobTitle, string Type, DateTimeOffset At);
internal sealed record ProjectActivityBatch(Guid SubjectId, long GrantRevision, List<ProjectActivityEntry> Entries);

public sealed partial class StateStore
{
    internal ProjectActivityBatch ReadProjectActivity(string authorization, Guid projectId, ProjectActivityBatch? previous)
    {
        var verifier = ReadViewerVerifier(authorization) ?? throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var access = ReadProjectAccess(db, tx, verifier, projectId);
        if (access.SubjectId is not { } subject) throw new ScopedProjectRejected(DraftGoalStatus.Unauthorized);
        if (!access.CanRead) throw new ScopedProjectRejected(DraftGoalStatus.NotFound);
        using var revision = Command(db, tx, """
            SELECT COALESCE(MAX(id),0) FROM viewer_access_audit WHERE subject_id=$subject AND project_id=$project;
            """, ("$subject", subject), ("$project", projectId));
        var grantRevision = (long)revision.ExecuteScalar()!;
        if (previous is not null && (previous.SubjectId != subject || previous.GrantRevision != grantRevision))
            throw new ScopedProjectRejected(DraftGoalStatus.Forbidden);

        // Jobs cannot move between projects. Unattributed events and free-text messages stay administrative.
        var entries = ReadViewerRows(db, tx, projectId, """
            SELECT e.id,j.id,j.title,e.type,e.at FROM events e JOIN jobs j ON j.id=e.job_id
            WHERE j.project_id=$project AND e.type IN (
                'queued','claimed','stage','job_updated','job_control','job_control_requested',
                'reconciliation_required','reconciled','scope_violation','scope_amended',
                'remote_claim','remote_complete','remote_rejected','remote_verified','integrated',
                'provider_budget_held','provider_budget_reconciled','budget_held','budget_reconciled')
            ORDER BY e.position DESC,e.id DESC LIMIT 100;
            """, row => new ProjectActivityEntry(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)),
                row.GetString(2), row.GetString(3), (DateTimeOffset)FromDb(row.GetValue(4), typeof(DateTimeOffset))!));
        return new(subject, grantRevision, entries);
    }
}
