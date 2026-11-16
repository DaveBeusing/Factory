using System.Text.Json.Serialization;

namespace Factory;

internal sealed record DashboardHealth(DateTimeOffset ServerTime, bool QueuePaused, int Projects,
    int ActiveJobs, int WorkersOnline, int WorkersTotal, DateTimeOffset? LastEventAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StateRevision { get; init; }
}

public sealed partial class StateStore
{
    private static void UpgradeDashboardHealthIndexes(Microsoft.Data.Sqlite.SqliteConnection db,
        Microsoft.Data.Sqlite.SqliteTransaction tx) => Execute(db, tx, """
            CREATE INDEX IF NOT EXISTS idx_jobs_health ON jobs(state,julianday(lease_until));
            CREATE INDEX IF NOT EXISTS idx_workers_health ON workers(julianday(heartbeat));
            """);

    internal void SetQueuePaused(bool paused)
    {
        using var db = Open();
        Execute(db, null, "UPDATE factory_metadata SET paused=$paused WHERE id=1;", ("$paused", paused));
    }

    internal DashboardHealth ReadDashboardHealth(DateTimeOffset now)
    {
        using var db = Open();
        using var query = Command(db, null, """
            SELECT paused,
                (SELECT COUNT(*) FROM projects WHERE status='active'),
                (SELECT COUNT(*) FROM jobs WHERE julianday(lease_until)>julianday($now)
                    AND state IN ('preparing','building','gating','reviewing','reworking','awaiting_approval')),
                (SELECT COUNT(*) FROM workers WHERE julianday(heartbeat)>julianday($onlineSince)),
                (SELECT COUNT(*) FROM workers),
                (SELECT at FROM events ORDER BY position DESC LIMIT 1)
            FROM factory_metadata WHERE id=1;
            """, ("$now", now), ("$onlineSince", now.AddSeconds(-20)));
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new InvalidOperationException("Factory metadata missing");
        return new(now, row.GetBoolean(0), row.GetInt32(1), row.GetInt32(2), row.GetInt32(3), row.GetInt32(4),
            (DateTimeOffset?)FromDb(row.GetValue(5), typeof(DateTimeOffset?)));
    }
}
