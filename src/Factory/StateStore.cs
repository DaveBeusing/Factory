using Microsoft.Data.Sqlite;

namespace Factory;

public sealed class StateStore
{
    public string Home { get; }
    private readonly string _file;
    public StateStore(string home)
    {
        Home = Path.GetFullPath(Environment.ExpandEnvironmentVariables(home));
        Directory.CreateDirectory(Home);
        _file = Path.Combine(Home, "factory.sqlite3");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS factory_state(id INTEGER PRIMARY KEY CHECK(id = 1), content TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 30
        }.ToString());
        connection.Open();
        using var pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
        pragmas.ExecuteNonQuery();
        return connection;
    }
    private static FactoryState Load(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT content FROM factory_state WHERE id = 1";
        var raw = cmd.ExecuteScalar() as string;
        return raw == null ? new FactoryState() : System.Text.Json.JsonSerializer.Deserialize<FactoryState>(raw, Json.Options) ?? new FactoryState();
    }
    public FactoryState Read()
    {
        using var c = Open();
        return Load(c, null);
    }
    public T Change<T>(Func<FactoryState, T> mutator)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var state = Load(c, tx);
        var result = mutator(state);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO factory_state(id,content) VALUES(1,$v) ON CONFLICT(id) DO UPDATE SET content=excluded.content";
        cmd.Parameters.AddWithValue("$v", Json.Stringify(state));
        cmd.ExecuteNonQuery();
        tx.Commit();
        return result;
    }
    public void Change(Action<FactoryState> action) => Change<int>(s => { action(s); return 0; });
    public void Event(Guid? jobId, string type, string message) => Change(s =>
    {
        s.Events.Add(new FactoryEvent { JobId = jobId, Type = type, Message = message[..Math.Min(message.Length, 2000)] });
        if (s.Events.Count > 20000) s.Events.RemoveRange(0, s.Events.Count - 20000);
    });
    public FactoryJob Job(Guid id) => Read().Jobs.FirstOrDefault(j => j.Id == id)
        ?? throw new ArgumentException($"Unknown job: {id}");
    public ProjectInfo Project(string key) => Read().Projects.FirstOrDefault(p =>
        p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || p.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown project: {key}");
    public void UpdateJob(Guid id, Action<FactoryJob> action) => Change(s =>
    {
        var j = s.Jobs.Single(x => x.Id == id);
        action(j); j.UpdatedAt = DateTimeOffset.UtcNow;
    });
    public FactoryJob AddJob(FactoryJob job) => Change(s =>
    {
        if (!s.Projects.Any(p => p.Id == job.ProjectId)) throw new ArgumentException("Project does not exist");
        foreach (var dependency in job.DependsOn)
            if (!s.Jobs.Any(j => j.Id == dependency && j.ProjectId == job.ProjectId))
                throw new ArgumentException($"Invalid dependency: {dependency}");
        if (DependencyGraph.WouldCycle(s.Jobs.Append(job).ToList())) throw new ArgumentException("Dependency cycle detected");
        s.Jobs.Add(job);
        s.Events.Add(new FactoryEvent { JobId = job.Id, Type = "queued", Message = job.Title });
        return job;
    });
}
