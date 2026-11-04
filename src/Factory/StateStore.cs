using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Factory;

/// <summary>
/// Relational SQLite persistence with transactional snapshot mutations.
/// The public snapshot API is retained so existing orchestrator callers stay unchanged.
/// Only changed entities are written; collection values live in ordered child tables.
/// </summary>
public sealed class StateStore
{
    private const int DatabaseVersion = 6;
    private sealed record EntityMap(string Table, string Collection, Type Type);

    // Order places referenced entities before their dependents.
    private static readonly EntityMap[] Entities =
    [
        new("projects", nameof(FactoryState.Projects), typeof(ProjectInfo)),
        new("goals", nameof(FactoryState.Goals), typeof(FactoryGoal)),
        new("jobs", nameof(FactoryState.Jobs), typeof(FactoryJob)),
        new("goal_budget_reservations", nameof(FactoryState.BudgetReservations), typeof(GoalBudgetReservation)),
        new("goal_budget_extensions", nameof(FactoryState.BudgetExtensions), typeof(GoalBudgetExtension)),
        new("knowledge_items", nameof(FactoryState.Knowledge), typeof(KnowledgeItem)),
        new("graph_nodes", nameof(FactoryState.Graph), typeof(GraphNode)),
        new("evaluations", nameof(FactoryState.Evaluations), typeof(EvaluationResult)),
        new("releases", nameof(FactoryState.Releases), typeof(ReleaseInfo)),
        new("workers", nameof(FactoryState.Workers), typeof(WorkerInfo)),
        new("worker_project_grants", nameof(FactoryState.WorkerProjectGrants), typeof(WorkerProjectGrant)),
        new("worker_capability_proofs", nameof(FactoryState.WorkerCapabilityProofs), typeof(WorkerCapabilityProof)),
        new("events", nameof(FactoryState.Events), typeof(FactoryEvent))
    ];

    public string Home { get; }
    private readonly string _file;

    public StateStore(string home)
    {
        Home = Path.GetFullPath(Environment.ExpandEnvironmentVariables(home));
        Directory.CreateDirectory(Home);
        _file = Path.Combine(Home, "factory.sqlite3");
        using var connection = Open();
        Initialize(connection);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _file, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 30, ForeignKeys = true
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string Q(string identifier) => "\"" + identifier + "\"";

    private static string Column(string property)
    {
        var result = new StringBuilder();
        for (int i = 0; i < property.Length; i++)
        {
            if (i > 0 && char.IsUpper(property[i]) &&
                (char.IsLower(property[i - 1]) || char.IsDigit(property[i - 1])))
                result.Append('_');
            result.Append(char.ToLowerInvariant(property[i]));
        }
        return result.ToString();
    }

    private static PropertyInfo[] Scalars(EntityMap map) =>
        map.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => !typeof(IList).IsAssignableFrom(p.PropertyType)).ToArray();

    private static PropertyInfo[] Collections(EntityMap map) =>
        map.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => typeof(IList).IsAssignableFrom(p.PropertyType)).ToArray();

    private static string SqlType(Type property)
    {
        property = Nullable.GetUnderlyingType(property) ?? property;
        if (property == typeof(string) || property == typeof(Guid) || property == typeof(DateTimeOffset))
            return "TEXT";
        if (property == typeof(bool) || property == typeof(int) || property == typeof(long))
            return "INTEGER";
        throw new NotSupportedException("Unsupported relational property: " + property);
    }

    private static object DbValue(object? value) => value switch
    {
        null => DBNull.Value,
        Guid id => id.ToString("D"),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        bool flag => flag ? 1 : 0,
        _ => value
    };

    private static object? FromDb(object value, Type property)
    {
        if (value is DBNull) return null;
        var underlying = Nullable.GetUnderlyingType(property) ?? property;
        if (underlying == typeof(Guid)) return Guid.Parse((string)value);
        if (underlying == typeof(DateTimeOffset))
            return DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (underlying == typeof(bool)) return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        if (underlying == typeof(int)) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (underlying == typeof(long)) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (underlying == typeof(string)) return (string)value;
        throw new NotSupportedException("Unsupported relational property: " + property);
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] arguments)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in arguments)
            command.Parameters.AddWithValue(name, DbValue(value));
        return command;
    }

    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = Command(db, tx, sql, arguments);
        command.ExecuteNonQuery();
    }

    private static void CreateBudgetReservationTable(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, tx, """
            CREATE TABLE goal_budget_reservations(
                position INTEGER NOT NULL,
                id TEXT PRIMARY KEY,
                goal_id TEXT NOT NULL REFERENCES goals(id) ON DELETE NO ACTION,
                job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE NO ACTION,
                execution_generation INTEGER NOT NULL,
                lease_token TEXT NOT NULL,
                base_usage INTEGER NOT NULL,
                reserved INTEGER NOT NULL CHECK(reserved>=0),
                consumed INTEGER NOT NULL CHECK(consumed>=0),
                state TEXT NOT NULL CHECK(state IN ('active','held','released')),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX idx_goal_reservations_goal_state
                ON goal_budget_reservations(goal_id,state);
            CREATE INDEX idx_goal_reservations_job_generation
                ON goal_budget_reservations(job_id,execution_generation);
            """);
    }

    private static void CreateBudgetExtensionTable(SqliteConnection db, SqliteTransaction tx)
    {
        // Schema 5 installations contain existing reservations but no extension history.
        // CREATE IF NOT EXISTS also supports fresh relational installations in this version.
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS goal_budget_extensions(
                position INTEGER NOT NULL,
                id TEXT PRIMARY KEY,
                goal_id TEXT NOT NULL REFERENCES goals(id) ON DELETE NO ACTION,
                job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE NO ACTION,
                reservation_id TEXT NOT NULL REFERENCES goal_budget_reservations(id) ON DELETE NO ACTION,
                execution_generation INTEGER NOT NULL,
                worker_id TEXT NOT NULL,
                granted INTEGER NOT NULL CHECK(granted>=0),
                authorized_total INTEGER NOT NULL CHECK(authorized_total>=0),
                at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_goal_budget_extensions_reservation
                ON goal_budget_extensions(reservation_id,at);
            PRAGMA user_version=6;
            """);
    }

    private static void UpgradeWorkerAuthorization(SqliteConnection db, SqliteTransaction tx)
    {
        // Existing worker identities get no grants or trusted capability evidence.
        // The upgrade intentionally leaves every remote worker unprivileged.
        Execute(db, tx, """
            CREATE TABLE worker_project_grants(
                position INTEGER NOT NULL,
                id TEXT PRIMARY KEY,
                worker_id TEXT NOT NULL REFERENCES workers(id) ON DELETE CASCADE,
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE
            );
            CREATE TABLE worker_capability_proofs(
                position INTEGER NOT NULL,
                id TEXT PRIMARY KEY,
                worker_id TEXT NOT NULL REFERENCES workers(id) ON DELETE CASCADE,
                capability TEXT NOT NULL,
                evidence TEXT NOT NULL,
                verified_at TEXT NOT NULL
            );
            CREATE TABLE projects_required_execution_capabilities(
                owner_id TEXT NOT NULL,
                position INTEGER NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY(owner_id,position),
                FOREIGN KEY(owner_id) REFERENCES projects(id) ON DELETE CASCADE
            );
            CREATE TABLE jobs_required_execution_capabilities(
                owner_id TEXT NOT NULL,
                position INTEGER NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY(owner_id,position),
                FOREIGN KEY(owner_id) REFERENCES jobs(id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX idx_worker_project_grants_unique
                ON worker_project_grants(worker_id,project_id);
            CREATE UNIQUE INDEX idx_worker_capability_proofs_unique
                ON worker_capability_proofs(worker_id,capability);
            PRAGMA user_version=5;
            """);
    }

    private static void Initialize(SqliteConnection db)
    {
        // Serialize first-time initialization across concurrently starting processes.
        using var tx = db.BeginTransaction();
        using var version = Command(db, tx, "PRAGMA user_version;");
        var schemaVersion = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (schemaVersion == DatabaseVersion)
        {
            tx.Commit();
            return;
        }
        if (schemaVersion is 2 or 3)
        {
            if (schemaVersion == 2)
            {
                Execute(db, tx, """
                    ALTER TABLE jobs ADD COLUMN execution_generation INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE jobs ADD COLUMN lease_token TEXT NOT NULL DEFAULT '';
                    ALTER TABLE jobs ADD COLUMN worker_process_id INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE jobs ADD COLUMN worker_machine_name TEXT NOT NULL DEFAULT '';
                    ALTER TABLE jobs ADD COLUMN worker_process_started_at TEXT;
                    ALTER TABLE jobs ADD COLUMN checkpoint_stage TEXT NOT NULL DEFAULT '';
                    ALTER TABLE jobs ADD COLUMN checkpoint_commit TEXT NOT NULL DEFAULT '';
                    ALTER TABLE jobs ADD COLUMN checkpoint_at TEXT;
                    """);
            }
            Execute(db, tx, "ALTER TABLE jobs ADD COLUMN usage_complete INTEGER NOT NULL DEFAULT 0;");
            CreateBudgetReservationTable(db, tx);
            // Pre-ledger in-flight work has unbounded/unknown usage. Fence it and hold
            // all remaining capacity until an operator reconciles its final usage.
            using (var current = Command(db, tx, """
                SELECT j.id, j.goal_id, j.execution_generation, j.lease_token, j.tokens_in, j.tokens_out
                FROM jobs j WHERE j.goal_id IS NOT NULL AND j.lease_owner != ''
                AND j.state IN ('preparing','building','gating','reviewing','reworking','awaiting_approval')
                ORDER BY j.goal_id, j.position;
                """))
            using (var reader = current.ExecuteReader())
            {
                var legacy = new List<(string Id, string Goal, long Generation, string Token, long Used)>();
                while (reader.Read())
                    legacy.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                        reader.GetString(3), checked(reader.GetInt64(4) + reader.GetInt64(5))));
                var position = 0;
                foreach (var group in legacy.GroupBy(x => x.Goal))
                {
                    using var remainingCommand = Command(db, tx, """
                        SELECT g.token_budget - COALESCE(SUM(j.tokens_in + j.tokens_out),0)
                        FROM goals g LEFT JOIN jobs j ON j.goal_id = g.id
                        WHERE g.id = $goal GROUP BY g.id;
                        """, ("$goal", group.Key));
                    var available = Math.Max(0, Convert.ToInt64(remainingCommand.ExecuteScalar(),
                        CultureInfo.InvariantCulture));
                    foreach (var item in group)
                    {
                        var held = available;
                        available = 0;
                        Execute(db, tx, """
                            INSERT INTO goal_budget_reservations(
                              position,id,goal_id,job_id,execution_generation,lease_token,
                              base_usage,reserved,consumed,state,created_at,updated_at)
                            VALUES($position,$id,$goal,$job,$generation,$token,$base,$reserved,0,
                                   'held',$now,$now);
                            """,
                            ("$position", position++), ("$id", Guid.NewGuid()),
                            ("$goal", group.Key), ("$job", item.Id), ("$generation", item.Generation),
                            ("$token", item.Token), ("$base", item.Used),
                            ("$reserved", held), ("$now", DateTimeOffset.UtcNow));
                    }
                }
            }
            Execute(db, tx, """
                UPDATE jobs SET state='blocked', stage='reconcile',
                    error='Pre-ledger execution needs budget and process reconciliation',
                    execution_generation=execution_generation+1, lease_owner='',lease_token='',lease_until=NULL
                WHERE goal_id IS NOT NULL AND lease_owner != ''
                AND state IN ('preparing','building','gating','reviewing','reworking','awaiting_approval');
                PRAGMA user_version=4;
                """);
            UpgradeWorkerAuthorization(db, tx);
            CreateBudgetExtensionTable(db, tx);
            tx.Commit();
            return;
        }
        if (schemaVersion == 4)
        {
            UpgradeWorkerAuthorization(db, tx);
            CreateBudgetExtensionTable(db, tx);
            tx.Commit();
            return;
        }
        if (schemaVersion == 5)
        {
            CreateBudgetExtensionTable(db, tx);
            tx.Commit();
            return;
        }
        if (schemaVersion != 0)
            throw new InvalidOperationException("Unsupported Factory database schema version " + schemaVersion +
                "; create a new FACTORY_HOME or restore a matching installation.");

        using var existing = Command(db, tx,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';");
        if (Convert.ToInt64(existing.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException(
                "Existing/legacy Factory SQLite database detected. Back up .factory, then use a new " +
                "FACTORY_HOME. The relational schema intentionally has no automatic JSON migration.");

        Execute(db, tx, """
            CREATE TABLE factory_metadata(
                id INTEGER PRIMARY KEY CHECK(id=1),
                schema INTEGER NOT NULL,
                paused INTEGER NOT NULL CHECK(paused IN (0,1))
            );
            INSERT INTO factory_metadata(id, schema, paused) VALUES(1,1,0);
            """);

        foreach (var map in Entities)
        {
            var definitions = new List<string> { "\"position\" INTEGER NOT NULL" };
            foreach (var property in Scalars(map))
            {
                var name = Column(property.Name);
                var type = SqlType(property.PropertyType);
                definitions.Add(Q(name) + " " + type + (property.Name == "Id" ? " PRIMARY KEY" :
                    Nullable.GetUnderlyingType(property.PropertyType) != null ? "" : " NOT NULL"));
            }
            if (map.Type.GetProperty("ProjectId") != null)
                definitions.Add("FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE NO ACTION");
            if (map.Type == typeof(FactoryJob))
                definitions.Add("FOREIGN KEY(goal_id) REFERENCES goals(id) ON DELETE NO ACTION");
            if (map.Type == typeof(GoalBudgetReservation) || map.Type == typeof(GoalBudgetExtension))
            {
                definitions.Add("FOREIGN KEY(goal_id) REFERENCES goals(id) ON DELETE NO ACTION");
                definitions.Add("FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE NO ACTION");
            }
            if (map.Type == typeof(GoalBudgetExtension))
                definitions.Add("FOREIGN KEY(reservation_id) REFERENCES goal_budget_reservations(id) ON DELETE NO ACTION");
            if (map.Type == typeof(GraphNode))
                definitions.Add("UNIQUE(project_id, file)");
            if (map.Type == typeof(WorkerProjectGrant) || map.Type == typeof(WorkerCapabilityProof))
                definitions.Add("FOREIGN KEY(worker_id) REFERENCES workers(id) ON DELETE CASCADE");
            Execute(db, tx, "CREATE TABLE " + Q(map.Table) + "(" + string.Join(", ", definitions) + ");");
            foreach (var property in Collections(map))
            {
                if (map.Type == typeof(ProjectInfo) && property.Name == nameof(ProjectInfo.Checks))
                    continue;
                var childTable = map.Table + "_" + Column(property.Name);
                var element = property.PropertyType.GenericTypeArguments.Single();
                var valueForeignKey = map.Type == typeof(FactoryJob) &&
                    property.Name == nameof(FactoryJob.DependsOn)
                    ? ", FOREIGN KEY(value) REFERENCES jobs(id) ON DELETE NO ACTION"
                    : "";
                Execute(db, tx, "CREATE TABLE " + Q(childTable) + "(" +
                    "owner_id TEXT NOT NULL, position INTEGER NOT NULL, value " + SqlType(element) +
                    " NOT NULL, PRIMARY KEY(owner_id, position), FOREIGN KEY(owner_id) REFERENCES " +
                    Q(map.Table) + "(id) ON DELETE CASCADE" + valueForeignKey + ");");
            }
        }

        Execute(db, tx, """
            CREATE TABLE project_checks(
                owner_id TEXT NOT NULL,
                position INTEGER NOT NULL,
                name TEXT NOT NULL,
                executable TEXT NOT NULL,
                container_image TEXT NOT NULL,
                timeout_seconds INTEGER NOT NULL,
                PRIMARY KEY(owner_id, position),
                FOREIGN KEY(owner_id) REFERENCES projects(id) ON DELETE CASCADE
            );
            CREATE TABLE project_check_arguments(
                owner_id TEXT NOT NULL,
                check_position INTEGER NOT NULL,
                position INTEGER NOT NULL,
                value TEXT NOT NULL,
                PRIMARY KEY(owner_id, check_position, position),
                FOREIGN KEY(owner_id, check_position)
                    REFERENCES project_checks(owner_id,position) ON DELETE CASCADE
            );
            CREATE INDEX idx_jobs_project_state ON jobs(project_id,state);
            CREATE INDEX idx_jobs_goal ON jobs(goal_id);
            CREATE INDEX idx_goal_reservations_goal_state ON goal_budget_reservations(goal_id,state);
            CREATE INDEX idx_goal_reservations_job_generation ON goal_budget_reservations(job_id,execution_generation);
            CREATE INDEX idx_jobs_lease ON jobs(lease_owner,lease_until);
            CREATE INDEX idx_goals_project ON goals(project_id,state);
            CREATE INDEX idx_events_time ON events(at);
            CREATE INDEX idx_knowledge_project ON knowledge_items(project_id);
            CREATE INDEX idx_releases_project ON releases(project_id);
            CREATE INDEX idx_workers_heartbeat ON workers(heartbeat);
            CREATE UNIQUE INDEX idx_worker_project_grants_unique
                ON worker_project_grants(worker_id,project_id);
            CREATE UNIQUE INDEX idx_worker_capability_proofs_unique
                ON worker_capability_proofs(worker_id,capability);
            PRAGMA user_version=5;
            """);
        CreateBudgetExtensionTable(db, tx);
        tx.Commit();
    }

    private static IList Items(FactoryState state, EntityMap map) =>
        (IList)(typeof(FactoryState).GetProperty(map.Collection)!.GetValue(state)
            ?? throw new InvalidOperationException("Missing Factory collection: " + map.Collection));

    private static string Key(object entity) =>
        Convert.ToString(entity.GetType().GetProperty("Id")!.GetValue(entity), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("Missing entity ID");

    private static FactoryState Load(SqliteConnection db, SqliteTransaction tx)
    {
        var state = new FactoryState();
        using (var meta = Command(db, tx, "SELECT schema,paused FROM factory_metadata WHERE id=1;"))
        using (var reader = meta.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Factory metadata missing");
            state.Schema = reader.GetInt32(0);
            state.Paused = reader.GetInt64(1) != 0;
        }

        foreach (var map in Entities)
        {
            var columns = Scalars(map);
            var byId = new Dictionary<string, object>(StringComparer.Ordinal);
            var fields = string.Join(",", columns.Select(p => Q(Column(p.Name))));
            using (var cmd = Command(db, tx, "SELECT " + fields + " FROM " + Q(map.Table) +
                " ORDER BY position,id;"))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var entity = Activator.CreateInstance(map.Type)!;
                    for (int i = 0; i < columns.Length; i++)
                        columns[i].SetValue(entity, FromDb(reader.GetValue(i), columns[i].PropertyType));
                    // Some models (e.g. ProjectInfo.ProtectedPaths) initialize lists
                    // with defaults. Database values must replace, never append to them.
                    foreach (var listProperty in Collections(map))
                        ((IList)listProperty.GetValue(entity)!).Clear();
                    Items(state, map).Add(entity);
                    byId.Add(Key(entity), entity);
                }
            }

            foreach (var property in Collections(map))
            {
                if (map.Type == typeof(ProjectInfo) && property.Name == nameof(ProjectInfo.Checks))
                    continue;
                var table = map.Table + "_" + Column(property.Name);
                var elementType = property.PropertyType.GenericTypeArguments.Single();
                using var cmd = Command(db, tx,
                    "SELECT owner_id,value FROM " + Q(table) + " ORDER BY owner_id,position;");
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var owner = reader.GetString(0);
                    if (!byId.TryGetValue(owner, out var entity))
                        throw new InvalidOperationException("Orphaned child row in " + table);
                    ((IList)property.GetValue(entity)!).Add(FromDb(reader.GetValue(1), elementType));
                }
            }
            if (map.Type == typeof(ProjectInfo))
                LoadChecks(db, tx, byId);
        }
        return state;
    }

    private static void LoadChecks(SqliteConnection db, SqliteTransaction tx,
        Dictionary<string, object> projects)
    {
        var checks = new Dictionary<(string Owner, int Position), CheckCommand>();
        using (var cmd = Command(db, tx,
            "SELECT owner_id,position,name,executable,container_image,timeout_seconds " +
            "FROM project_checks ORDER BY owner_id,position;"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var owner = reader.GetString(0);
                if (!projects.TryGetValue(owner, out var entity))
                    throw new InvalidOperationException("Orphaned project check");
                var check = new CheckCommand
                {
                    Name = reader.GetString(2), Executable = reader.GetString(3),
                    ContainerImage = reader.GetString(4), TimeoutSeconds = reader.GetInt32(5)
                };
                var position = reader.GetInt32(1);
                ((ProjectInfo)entity).Checks.Add(check);
                checks.Add((owner, position), check);
            }
        }
        using (var cmd = Command(db, tx,
            "SELECT owner_id,check_position,value FROM project_check_arguments " +
            "ORDER BY owner_id,check_position,position;"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                checks[(reader.GetString(0), reader.GetInt32(1))].Arguments.Add(reader.GetString(2));
    }

    public FactoryState Read()
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var state = Load(db, tx);
        tx.Commit();
        return state;
    }

    private static void Upsert(SqliteConnection db, SqliteTransaction tx, EntityMap map, object entity, int position)
    {
        var scalar = Scalars(map);
        var names = new List<string> { "position" };
        names.AddRange(scalar.Select(p => Column(p.Name)));
        var arguments = new List<(string Name, object? Value)> { ("$position", position) };
        arguments.AddRange(scalar.Select(p => ("$" + Column(p.Name), p.GetValue(entity))));
        var columns = string.Join(",", names.Select(Q));
        var bindings = string.Join(",", names.Select(n => "$" + n));
        var updates = string.Join(",", names.Where(n => n != "id").Select(n => Q(n) + "=excluded." + Q(n)));
        Execute(db, tx, "INSERT INTO " + Q(map.Table) + "(" + columns + ") VALUES(" + bindings +
            ") ON CONFLICT(id) DO UPDATE SET " + updates + ";", arguments.ToArray());

        foreach (var property in Collections(map))
        {
            if (map.Type == typeof(ProjectInfo) && property.Name == nameof(ProjectInfo.Checks))
                continue;
            var table = map.Table + "_" + Column(property.Name);
            Execute(db, tx, "DELETE FROM " + Q(table) + " WHERE owner_id=$owner;", ("$owner", Key(entity)));
            var values = (IList)property.GetValue(entity)!;
            for (int i = 0; i < values.Count; i++)
                Execute(db, tx, "INSERT INTO " + Q(table) +
                    "(owner_id,position,value) VALUES($owner,$position,$value);",
                    ("$owner", Key(entity)), ("$position", i), ("$value", values[i]));
        }

        if (map.Type == typeof(ProjectInfo))
        {
            Execute(db, tx, "DELETE FROM project_checks WHERE owner_id=$owner;", ("$owner", Key(entity)));
            var checks = ((ProjectInfo)entity).Checks;
            for (int i = 0; i < checks.Count; i++)
            {
                var check = checks[i];
                Execute(db, tx, "INSERT INTO project_checks " +
                    "(owner_id,position,name,executable,container_image,timeout_seconds) " +
                    "VALUES($owner,$position,$name,$executable,$container,$timeout);",
                    ("$owner", Key(entity)), ("$position", i), ("$name", check.Name),
                    ("$executable", check.Executable), ("$container", check.ContainerImage),
                    ("$timeout", check.TimeoutSeconds));
                for (int j = 0; j < check.Arguments.Count; j++)
                    Execute(db, tx, "INSERT INTO project_check_arguments " +
                        "(owner_id,check_position,position,value) VALUES($owner,$check,$position,$value);",
                        ("$owner", Key(entity)), ("$check", i), ("$position", j),
                        ("$value", check.Arguments[j]));
            }
        }
    }

    private static void Save(SqliteConnection db, SqliteTransaction tx, FactoryState state,
        IReadOnlyList<Dictionary<string, string>> before)
    {
        Execute(db, tx, "UPDATE factory_metadata SET schema=$schema, paused=$paused WHERE id=1;",
            ("$schema", state.Schema), ("$paused", state.Paused));

        // Defer cross-entity FK checks to the end of this atomic mutation, since
        // a plan may insert its goals, jobs and dependencies in one transaction.
        Execute(db, tx, "PRAGMA defer_foreign_keys=ON;");

        for (int index = 0; index < Entities.Length; index++)
        {
            var map = Entities[index];
            var collection = Items(state, map);
            var old = before[index];
            var current = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entity in collection)
                if (!current.Add(Key(entity!)))
                    throw new InvalidOperationException("Duplicate relational entity ID: " + Key(entity!));
            foreach (var removed in old.Keys.Where(id => !current.Contains(id)))
                Execute(db, tx, "DELETE FROM " + Q(map.Table) + " WHERE id=$id;", ("$id", removed));
            for (int i = 0; i < collection.Count; i++)
            {
                var entity = collection[i]!;
                var key = Key(entity);
                var serialized = JsonSerializer.Serialize(entity, map.Type, Json.Options);
                // A changed position must also be persisted, even for identical objects.
                if (old.TryGetValue(key, out var previous) && previous == i + ":" + serialized)
                    continue;
                Upsert(db, tx, map, entity, i);
            }
        }
    }

    public T Change<T>(Func<FactoryState, T> mutator)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var state = Load(db, tx);
        var before = Entities.Select(map =>
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            var collection = Items(state, map);
            for (int i = 0; i < collection.Count; i++)
            {
                var item = collection[i]!;
                if (!snapshot.TryAdd(Key(item), i + ":" +
                    JsonSerializer.Serialize(item, map.Type, Json.Options)))
                    throw new InvalidOperationException("Duplicate relational entity ID: " + Key(item));
            }
            return snapshot;
        }).ToArray();
        var result = mutator(state);
        Save(db, tx, state, before);
        tx.Commit();
        return result;
    }

    public void Change(Action<FactoryState> action) => Change<int>(s => { action(s); return 0; });

    /// <summary>
    /// Append a single event without materializing unrelated state. The monotonic position
    /// and bounded retention are committed together under SQLite's write transaction.
    /// </summary>
    public void Event(Guid? jobId, string type, string message)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var next = Command(db, tx, "SELECT COALESCE(MAX(position),-1)+1 FROM events;"))
        {
            var position = Convert.ToInt64(next.ExecuteScalar(), CultureInfo.InvariantCulture);
            Execute(db, tx, """
                INSERT INTO events(position,id,at,job_id,type,message)
                VALUES($position,$id,$at,$job,$type,$message);
                """, ("$position", position), ("$id", Guid.NewGuid()),
                ("$at", DateTimeOffset.UtcNow), ("$job", jobId), ("$type", type),
                ("$message", message[..Math.Min(message.Length, 2000)]));
        }
        Execute(db, tx, """
            DELETE FROM events WHERE id IN (
                SELECT id FROM events ORDER BY position DESC LIMIT -1 OFFSET 20000
            );
            """);
        tx.Commit();
    }

    /// <summary>
    /// Persist a fenced job lifecycle transition and its audit event atomically.
    /// This hot path avoids reading or comparing unrelated Factory entities.
    /// A superseded, expired, or inactive execution cannot change the job.
    /// </summary>
    public void SetFencedStage(Guid jobId, ExecutionFence fence, string nextState, string stage,
        string? error = null)
    {
        if (string.IsNullOrWhiteSpace(nextState) || string.IsNullOrWhiteSpace(stage))
            throw new ArgumentException("Job state and stage are required");
        var now = DateTimeOffset.UtcNow;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var update = Command(db, tx, """
            UPDATE jobs SET state=$state, stage=$stage, error=COALESCE($error,error),
                updated_at=$now, checkpoint_stage=$stage, checkpoint_at=$now
            WHERE id=$id AND execution_generation=$generation AND lease_token=$token
              AND lease_owner=$owner AND lease_until>$now
              AND state IN ('preparing','building','gating','reviewing','reworking','awaiting_approval');
            """, ("$state", nextState), ("$stage", stage), ("$error", error),
            ("$now", now), ("$id", jobId), ("$generation", fence.Generation),
            ("$token", fence.Token), ("$owner", fence.Owner)))
        {
            if (update.ExecuteNonQuery() != 1)
                throw new LostExecutionLeaseException("Execution lease expired or generation was fenced");
        }

        using (var next = Command(db, tx, "SELECT COALESCE(MAX(position),-1)+1 FROM events;"))
        {
            var position = Convert.ToInt64(next.ExecuteScalar(), CultureInfo.InvariantCulture);
            Execute(db, tx, """
                INSERT INTO events(position,id,at,job_id,type,message)
                VALUES($position,$id,$at,$job,'stage',$message);
                """, ("$position", position), ("$id", Guid.NewGuid()), ("$at", now),
                ("$job", jobId), ("$message", nextState + "/" + stage));
        }
        Execute(db, tx, """
            DELETE FROM events WHERE id IN (
                SELECT id FROM events ORDER BY position DESC LIMIT -1 OFFSET 20000
            );
            """);
        tx.Commit();
    }

    /// <summary>
    /// Debit a single fenced execution and its active goal reservation atomically.
    /// Only the job and its current reservation are read/written; observed overrun
    /// is persisted rather than hidden, and is returned as a negative balance.
    /// The caller must interrupt/block an execution whose balance is exhausted.
    /// </summary>
    public long RecordFencedUsage(Guid jobId, ExecutionFence fence, long inputDelta,
        long outputDelta, bool? usageComplete = null)
    {
        if (inputDelta < 0 || outputDelta < 0)
            throw new ArgumentOutOfRangeException(nameof(inputDelta), "Token deltas must be nonnegative");
        var now = DateTimeOffset.UtcNow;
        using var db = Open();
        using var tx = db.BeginTransaction();
        long oldIn, oldOut;
        Guid? goalId;
        using (var select = Command(db, tx, """
            SELECT tokens_in,tokens_out,goal_id FROM jobs
            WHERE id=$id AND execution_generation=$generation AND lease_token=$token
                AND lease_owner=$owner AND lease_until>$now
                AND state IN ('preparing','building','gating','reviewing','reworking','awaiting_approval');
            """, ("$id", jobId), ("$generation", fence.Generation),
            ("$token", fence.Token), ("$owner", fence.Owner), ("$now", now)))
        using (var reader = select.ExecuteReader())
        {
            if (!reader.Read())
                throw new LostExecutionLeaseException("Execution lease expired or generation was fenced");
            oldIn = reader.GetInt64(0);
            oldOut = reader.GetInt64(1);
            goalId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));
        }

        var newIn = checked(oldIn + inputDelta);
        var newOut = checked(oldOut + outputDelta);
        var remaining = long.MaxValue;
        if (goalId is { } goal)
        {
            Guid reservationId;
            long baseUsage, reserved, previousConsumed;
            using (var select = Command(db, tx, """
                SELECT id,base_usage,reserved,consumed FROM goal_budget_reservations
                WHERE job_id=$job AND goal_id=$goal AND execution_generation=$generation
                    AND lease_token=$token AND state='active';
                """, ("$job", jobId), ("$goal", goal),
                ("$generation", fence.Generation), ("$token", fence.Token)))
            using (var reader = select.ExecuteReader())
            {
                if (!reader.Read())
                    throw new TokenBudgetExceededException("No active fenced goal reservation");
                reservationId = Guid.Parse(reader.GetString(0));
                baseUsage = reader.GetInt64(1);
                reserved = reader.GetInt64(2);
                previousConsumed = reader.GetInt64(3);
                if (reader.Read())
                    throw new InvalidOperationException("Multiple active reservations for one execution");
            }
            var consumed = checked(newIn + newOut - baseUsage);
            if (consumed < previousConsumed)
                throw new InvalidOperationException("Goal reservation usage cannot regress");
            Execute(db, tx, """
                UPDATE goal_budget_reservations SET consumed=$consumed,updated_at=$now
                WHERE id=$id AND state='active';
                """, ("$consumed", consumed), ("$now", now), ("$id", reservationId));
            remaining = reserved - consumed;
        }

        using (var update = Command(db, tx, """
            UPDATE jobs SET tokens_in=$input,tokens_out=$output,
                usage_complete=COALESCE($complete,usage_complete)
            WHERE id=$id AND execution_generation=$generation AND lease_token=$token
                AND lease_owner=$owner AND lease_until>$now
                AND state IN ('preparing','building','gating','reviewing','reworking','awaiting_approval');
            """, ("$input", newIn), ("$output", newOut),
            ("$complete", usageComplete.HasValue ? (object)(usageComplete.Value ? 1 : 0) : null),
            ("$id", jobId), ("$generation", fence.Generation),
            ("$token", fence.Token), ("$owner", fence.Owner), ("$now", now)))
        {
            if (update.ExecuteNonQuery() != 1)
                throw new LostExecutionLeaseException("Execution lease expired or generation was fenced");
        }
        tx.Commit();
        return remaining;
    }

    /// <summary>
    /// Renew only the exact live execution lease. No snapshot is loaded, and a stale
    /// generation, token, owner, expired lease or terminal state cannot be renewed.
    /// </summary>
    public bool RenewLease(Guid jobId, ExecutionFence fence, int leaseSeconds)
    {
        if (leaseSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        var now = DateTimeOffset.UtcNow;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var command = Command(db, tx, """
            UPDATE jobs SET lease_until=$until
            WHERE id=$id AND execution_generation=$generation AND lease_token=$token
              AND lease_owner=$owner AND lease_until>$now
              AND state IN ('preparing','building','gating','reviewing','reworking','awaiting_approval');
            """, ("$until", now.AddSeconds(leaseSeconds)), ("$id", jobId),
            ("$generation", fence.Generation), ("$token", fence.Token),
            ("$owner", fence.Owner), ("$now", now));
        var updated = command.ExecuteNonQuery() == 1;
        tx.Commit();
        return updated;
    }

    /// <summary>
    /// Update an enabled worker's liveness observation without loading project/job graphs.
    /// Does not authorize a job-specific heartbeat or renew an execution lease.
    /// </summary>
    public bool TouchWorkerHeartbeat(string workerId)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var command = Command(db, tx,
            "UPDATE workers SET heartbeat=$now WHERE id=$id AND enabled=1;",
            ("$now", DateTimeOffset.UtcNow), ("$id", workerId));
        var updated = command.ExecuteNonQuery() == 1;
        tx.Commit();
        return updated;
    }

    /// <summary>Read a single job and its ordered child values in one read transaction.</summary>
    public FactoryJob Job(Guid id)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var map = Entities.Single(m => m.Type == typeof(FactoryJob));
        var scalar = Scalars(map);
        FactoryJob? job;
        using (var cmd = Command(db, tx, "SELECT " +
            string.Join(",", scalar.Select(p => Q(Column(p.Name)))) +
            " FROM jobs WHERE id=$id;", ("$id", id)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new ArgumentException($"Unknown job: {id}");
            job = new FactoryJob();
            for (int index = 0; index < scalar.Length; index++)
                scalar[index].SetValue(job,
                    FromDb(reader.GetValue(index), scalar[index].PropertyType));
        }
        foreach (var property in Collections(map))
        {
            var values = (IList)property.GetValue(job)!;
            values.Clear();
            var elementType = property.PropertyType.GenericTypeArguments.Single();
            using var cmd = Command(db, tx, "SELECT value FROM " +
                Q(map.Table + "_" + Column(property.Name)) +
                " WHERE owner_id=$id ORDER BY position;", ("$id", id));
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) values.Add(FromDb(reader.GetValue(0), elementType));
        }
        tx.Commit();
        return job;
    }

    public ProjectInfo Project(string key) => Read().Projects.FirstOrDefault(p =>
        p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
        p.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown project: {key}");

    public void UpdateJob(Guid id, Action<FactoryJob> action) => Change(s =>
    {
        var job = s.Jobs.Single(j => j.Id == id);
        action(job);
        job.UpdatedAt = DateTimeOffset.UtcNow;
    });

    public FactoryJob AddJob(FactoryJob job) => Change(s =>
    {
        if (!s.Projects.Any(p => p.Id == job.ProjectId))
            throw new ArgumentException("Project does not exist");
        foreach (var dependency in job.DependsOn)
            if (!s.Jobs.Any(j => j.Id == dependency && j.ProjectId == job.ProjectId))
                throw new ArgumentException($"Invalid dependency: {dependency}");
        if (DependencyGraph.WouldCycle(s.Jobs.Append(job).ToList()))
            throw new ArgumentException("Dependency cycle detected");
        s.Jobs.Add(job);
        s.Events.Add(new FactoryEvent { JobId = job.Id, Type = "queued", Message = job.Title });
        return job;
    });
}
