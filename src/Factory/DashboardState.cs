using Microsoft.Data.Sqlite;

namespace Factory;

internal sealed class DashboardStateRevision(SqliteConnection observer) : IDisposable
{
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private readonly object _gate = new();

    public string Current()
    {
        // data_version is comparable only on the same connection. A unique epoch also
        // invalidates validators held by a browser across controller restarts.
        lock (_gate)
        {
            using var command = observer.CreateCommand();
            command.CommandText = "PRAGMA data_version;";
            return $"\"{_epoch}-{command.ExecuteScalar()}\"";
        }
    }

    public void Dispose()
    {
        lock (_gate) observer.Dispose();
    }
}

public sealed partial class StateStore
{
    internal DashboardStateRevision WatchDashboardState() => new(Open());

    internal FactoryState ReadDashboardState()
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var state = Load(db, tx, dashboard: true);
        tx.Commit();
        return state;
    }

    internal List<FactoryEvent> ReadDashboardJobActivity(Guid jobId)
    {
        using var db = Open();
        return ReadDashboardEvents(db, null, 15, jobId: jobId);
    }

    internal List<FactoryEvent> ReadDashboardBudgetActivity(Guid goalId)
    {
        using var db = Open();
        return ReadDashboardEvents(db, null, 20, goalId: goalId);
    }

    private static List<FactoryEvent> ReadDashboardEvents(SqliteConnection db, SqliteTransaction? tx,
        int limit, Guid? jobId = null, Guid? goalId = null)
    {
        using var query = Command(db, tx, """
            SELECT id,at,job_id,type,message FROM (
                SELECT e.id,e.at,e.job_id,e.type,e.message,e.position FROM events e
                WHERE ($job IS NULL OR e.job_id=$job) AND ($goal IS NULL OR (
                    e.type IN ('budget_reserved','budget_extended','budget_held','budget_released',
                        'budget_reconciled','budget_overrun') AND
                    EXISTS (SELECT 1 FROM jobs j WHERE j.id=e.job_id AND j.goal_id=$goal)
                ))
                ORDER BY e.position DESC,e.id DESC LIMIT $limit
            ) ORDER BY position,id;
            """, ("$job", jobId), ("$goal", goalId), ("$limit", limit));
        using var rows = query.ExecuteReader();
        var result = new List<FactoryEvent>();
        while (rows.Read())
            result.Add(new FactoryEvent
            {
                Id = Guid.Parse(rows.GetString(0)), At = (DateTimeOffset)FromDb(rows.GetValue(1), typeof(DateTimeOffset))!,
                JobId = rows.IsDBNull(2) ? null : Guid.Parse(rows.GetString(2)),
                Type = rows.GetString(3), Message = rows.GetString(4)
            });
        return result;
    }
}
