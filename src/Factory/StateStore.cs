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
public sealed partial class StateStore
{
    private const int DatabaseVersion = 24;
    private sealed record EntityMap(string Table, string Collection, Type Type);

    // Order places referenced entities before their dependents.
    private static readonly EntityMap[] Entities =
    [
        new("projects", nameof(FactoryState.Projects), typeof(ProjectInfo)),
        new("goals", nameof(FactoryState.Goals), typeof(FactoryGoal)),
        new("jobs", nameof(FactoryState.Jobs), typeof(FactoryJob)),
        new("goal_budget_reservations", nameof(FactoryState.BudgetReservations), typeof(GoalBudgetReservation)),
        new("goal_budget_extensions", nameof(FactoryState.BudgetExtensions), typeof(GoalBudgetExtension)),
        new("provider_assignments", nameof(FactoryState.ProviderAssignments), typeof(ProviderAssignment)),
        new("provider_budget_limits", nameof(FactoryState.ProviderBudgetLimits), typeof(ProviderBudgetLimit)),
        new("provider_usages", nameof(FactoryState.ProviderUsages), typeof(ProviderUsageEntry)),
        new("provider_budget_reservations", nameof(FactoryState.ProviderBudgetReservations), typeof(ProviderBudgetReservation)),
        new("remote_provider_leases", nameof(FactoryState.RemoteProviderLeases), typeof(RemoteProviderLease)),
        new("remote_provider_turns", nameof(FactoryState.RemoteProviderTurns), typeof(RemoteProviderTurn)),
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
            .Where(p => p.CanWrite && !typeof(IList).IsAssignableFrom(p.PropertyType)).ToArray();

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

    private static void UpgradeProviders(SqliteConnection db, SqliteTransaction tx)
    {
        // Additive migration. Existing jobs have no reconstructable per-provider usage:
        // historical unattributed totals remain in the existing goal ledger.
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS provider_assignments(
                position INTEGER NOT NULL, id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE NO ACTION,
                role TEXT NOT NULL, provider_id TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_provider_assignment_role
                ON provider_assignments(project_id,role);
            CREATE TABLE IF NOT EXISTS provider_budget_limits(
                position INTEGER NOT NULL, id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE NO ACTION,
                provider_id TEXT NOT NULL, token_limit INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_provider_budget_project
                ON provider_budget_limits(project_id,provider_id);
            CREATE TABLE IF NOT EXISTS provider_usages(
                position INTEGER NOT NULL, id TEXT PRIMARY KEY,
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE NO ACTION,
                goal_id TEXT, job_id TEXT REFERENCES jobs(id) ON DELETE NO ACTION,
                execution_generation INTEGER NOT NULL,
                provider_id TEXT NOT NULL, model TEXT NOT NULL, role TEXT NOT NULL,
                input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
                final_usage_reported INTEGER NOT NULL, at TEXT NOT NULL, reported_cost_micros INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_provider_usage_scope
                ON provider_usages(provider_id,project_id,job_id);
            CREATE TABLE IF NOT EXISTS provider_budget_reservations(
                position INTEGER NOT NULL, id TEXT PRIMARY KEY,
                usage_id TEXT NOT NULL REFERENCES provider_usages(id) ON DELETE NO ACTION,
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE NO ACTION,
                provider_id TEXT NOT NULL, reserved INTEGER NOT NULL,
                consumed INTEGER NOT NULL, state TEXT NOT NULL,
                owner_process_id INTEGER NOT NULL, owner_machine TEXT NOT NULL, owner_started_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_provider_reservation_scope
                ON provider_budget_reservations(provider_id,project_id,state);
            PRAGMA user_version=9;
            """);
        UpgradeRemoteProviders(db, tx);
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

    private static void UpgradeRemoteProviders(SqliteConnection db, SqliteTransaction tx)
    {
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS remote_provider_leases(
                position INTEGER NOT NULL, id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(id),
                execution_generation INTEGER NOT NULL, worker_id TEXT NOT NULL,
                executor_url TEXT NOT NULL, route_json TEXT NOT NULL, turn_tokens INTEGER NOT NULL, max_job_tokens INTEGER NOT NULL,
                UNIQUE(job_id, execution_generation)
            );
            CREATE TABLE IF NOT EXISTS remote_provider_turns(
                position INTEGER NOT NULL, id TEXT PRIMARY KEY, lease_id TEXT NOT NULL REFERENCES remote_provider_leases(id),
                usage_id TEXT NOT NULL REFERENCES provider_usages(id), role TEXT NOT NULL,
                input_commit TEXT NOT NULL, output_tree TEXT NOT NULL, observed_model TEXT NOT NULL,
                state TEXT NOT NULL, started_at TEXT NOT NULL, grant_digest TEXT NOT NULL, hold_reason TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_remote_provider_turns_lease ON remote_provider_turns(lease_id);
            PRAGMA user_version=11;
            """);
    }

    private static void Initialize(SqliteConnection db)
    {
        // Serialize first-time initialization across concurrently starting processes.
        using var tx = db.BeginTransaction();
        using var version = Command(db, tx, "PRAGMA user_version;");
        var schemaVersion = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (schemaVersion != DatabaseVersion)
        {
            if (schemaVersion < 12 || schemaVersion > DatabaseVersion)
            {
                UpgradeToSchema11(db, tx, schemaVersion);
                UpgradeProjectReaders(db, tx);
            }
            if (schemaVersion < 13) UpgradeProjectPlanning(db, tx);
            if (schemaVersion < 14) UpgradePlanRevisions(db, tx);
            if (schemaVersion < 15) UpgradeProjectControls(db, tx);
            if (schemaVersion < 16) UpgradeProjectBudgetReconciliation(db, tx);
            if (schemaVersion < 17) UpgradeProjectGoalReconciliation(db, tx);
            if (schemaVersion < 18) UpgradeProjectReviews(db, tx);
            if (schemaVersion < 19) UpgradeLifecycleAudit(db, tx);
            if (schemaVersion < 20) UpgradeFactoryPolicy(db, tx);
            if (schemaVersion < 21) UpgradeIssueIntake(db, tx);
            if (schemaVersion < 22) Execute(db, tx, """
                CREATE TABLE IF NOT EXISTS jobs_shared_resources(
                    owner_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                    position INTEGER NOT NULL, value TEXT NOT NULL, PRIMARY KEY(owner_id,position)
                );
                INSERT INTO jobs_shared_resources(owner_id,position,value)
                SELECT id,0,'unknown' FROM jobs
                WHERE NOT EXISTS(SELECT 1 FROM jobs_shared_resources r WHERE r.owner_id=jobs.id);
                CREATE TABLE IF NOT EXISTS scheduling_integrations(
                    job_id TEXT PRIMARY KEY REFERENCES jobs(id) ON DELETE CASCADE,
                    project_id TEXT NOT NULL, head_sha TEXT NOT NULL, integrated_sha TEXT NOT NULL
                );
                INSERT OR IGNORE INTO scheduling_integrations(job_id,project_id,head_sha,integrated_sha)
                SELECT j.id,j.project_id,j.head_sha,a.reference FROM jobs j JOIN lifecycle_audit a
                    ON a.entity_id=j.id AND a.project_id=j.project_id
                WHERE j.integrated=1 AND j.state='integrated' AND a.entity_kind='job'
                    AND a.reason_code='IntegrationVerified' AND a.reference IS NOT NULL;
                PRAGMA user_version=22;
                """);
            if (schemaVersion < 23) UpgradeExecutionProfiles(db, tx);
            UpgradeReviewCycles(db, tx);
        }
        tx.Commit();
    }

    private static void UpgradeToSchema11(SqliteConnection db, SqliteTransaction tx, int schemaVersion)
    {
        if (schemaVersion == 11) return;
        if (schemaVersion == 10)
        {
            Execute(db, tx, """
                ALTER TABLE remote_provider_turns ADD COLUMN grant_digest TEXT NOT NULL DEFAULT '';
                ALTER TABLE remote_provider_turns ADD COLUMN hold_reason TEXT NOT NULL DEFAULT '';
                PRAGMA user_version=11;
                """);
            return;
        }
        if (schemaVersion == 9)
        {
            UpgradeRemoteProviders(db, tx);
            return;
        }
        if (schemaVersion is 7 or 8)
        {
            Execute(db, tx, "ALTER TABLE provider_usages ADD COLUMN reported_cost_micros INTEGER;");
            if (schemaVersion == 7)
            {
                // Version 7 recorded no invocation owner. Upgrades require stopped controllers.
                // Preserve uncertain usage as a held liability for explicit reconciliation.
                Execute(db, tx, """
                    ALTER TABLE provider_budget_reservations ADD COLUMN owner_process_id INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE provider_budget_reservations ADD COLUMN owner_machine TEXT NOT NULL DEFAULT '';
                    ALTER TABLE provider_budget_reservations ADD COLUMN owner_started_at TEXT;
                    UPDATE provider_budget_reservations SET state='held' WHERE state='active';
                    """);
            }
            UpgradeRemoteProviders(db, tx);
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
            UpgradeProviders(db, tx);
            return;
        }
        if (schemaVersion == 4)
        {
            UpgradeWorkerAuthorization(db, tx);
            CreateBudgetExtensionTable(db, tx);
            UpgradeProviders(db, tx);
            return;
        }
        if (schemaVersion == 5)
        {
            CreateBudgetExtensionTable(db, tx);
            UpgradeProviders(db, tx);
            return;
        }
        if (schemaVersion == 6)
        {
            UpgradeProviders(db, tx);
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

        foreach (var map in Entities.Where(map => map.Type != typeof(RemoteProviderLease) && map.Type != typeof(RemoteProviderTurn)))
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
        UpgradeProviders(db, tx);
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
        using var integration = Command(db, tx, """
            SELECT DISTINCT j.id FROM scheduling_integrations p JOIN jobs j ON j.id=p.job_id
            JOIN lifecycle_audit a ON a.entity_id=j.id AND a.reference=p.integrated_sha
            AND p.project_id=j.project_id AND lower(p.head_sha)=lower(j.head_sha)
            WHERE a.entity_kind='job' AND a.reason_code='IntegrationVerified' AND a.project_id=j.project_id
                AND j.state='integrated' AND j.integrated=1
                AND length(a.reference) IN (40,64) AND lower(a.reference) NOT GLOB '*[^0-9a-f]*'
                AND length(j.head_sha) IN (40,64) AND lower(j.head_sha) NOT GLOB '*[^0-9a-f]*';
            """);
        using var verified = integration.ExecuteReader();
        while (verified.Read()) state.VerifiedIntegrations.Add(Guid.Parse(verified.GetString(0)));
        using var starts = Command(db, tx, "SELECT job_id,generation FROM started_builder_claims;");
        using var started = starts.ExecuteReader();
        while (started.Read()) state.StartedBuilders.Add((Guid.Parse(started.GetString(0)), started.GetInt64(1)));
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

    public T Change<T>(Func<FactoryState, T> mutator) => Change((state, _, _) => mutator(state));

    internal T Change<T>(Func<FactoryState, SqliteConnection, SqliteTransaction, T> mutator)
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
        var result = mutator(state, db, tx);
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
        AppendEvent(db, tx, jobId, type, message);
        tx.Commit();
    }

    private static void AppendEvent(SqliteConnection db, SqliteTransaction tx, Guid? jobId, string type, string message)
    {
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
    }

    /// <summary>
    /// Persist a fenced job lifecycle transition and its audit event atomically.
    /// This hot path avoids reading or comparing unrelated Factory entities.
    /// A superseded, expired, or inactive execution cannot change the job.
    /// </summary>
    public void SetFencedStage(Guid jobId, ExecutionFence fence, string nextState, string stage,
        string? error = null) => WriteFencedStage(jobId, fence, nextState, stage, error);

    internal void AdvanceFencedStage(Guid jobId, ExecutionFence fence, FactoryJob expected,
        string nextState, string stage, string? error = null) =>
        WriteFencedStage(jobId, fence, nextState, stage, error, expected: expected);

    internal void InterruptFencedStage(Guid jobId, ExecutionFence fence) =>
        WriteFencedStage(jobId, fence, JobStates.Paused, "interrupted", null, interrupted: true);

    private void WriteFencedStage(Guid jobId, ExecutionFence fence, string nextState, string stage,
        string? error, bool interrupted = false, FactoryJob? expected = null)
    {
        if (string.IsNullOrWhiteSpace(nextState) || string.IsNullOrWhiteSpace(stage))
            throw new ArgumentException("Job state and stage are required");
        using var db = Open();
        using var tx = db.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        string previousState;
        string previousStage;
        string previousError;
        Guid projectId;
        string requestedState;
        DateTimeOffset? checkpointAt;
        using (var read = Command(db, tx, """
            SELECT state,stage,error,project_id,requested_state,checkpoint_at FROM jobs
            WHERE id=$id AND execution_generation=$generation AND lease_token=$token
              AND lease_owner=$owner AND lease_until>$now;
            """, ("$id", jobId), ("$generation", fence.Generation), ("$token", fence.Token),
            ("$owner", fence.Owner), ("$now", now)))
        using (var row = read.ExecuteReader())
        {
            if (!row.Read())
                throw new LostExecutionLeaseException("Execution lease expired or generation was fenced");
            previousState = row.GetString(0);
            previousStage = row.GetString(1);
            previousError = row.GetString(2);
            projectId = Guid.Parse(row.GetString(3));
            requestedState = row.GetString(4);
            checkpointAt = (DateTimeOffset?)FromDb(row.GetValue(5), typeof(DateTimeOffset?));
        }
        if (!JobStates.Active.Contains(previousState))
            throw new LostExecutionLeaseException("Execution is no longer active");
        if (interrupted)
        {
            if (previousStage is "commit" or "push" or "pr")
            {
                nextState = JobStates.Blocked;
                stage = "interrupted_git";
                error = "Git side effects interrupted; reconcile before retry";
            }
            else
            {
                nextState = requestedState switch
                {
                    LocalJobControl.ProjectCancelRequest => JobStates.Cancelled,
                    JobStates.Stopped or LocalJobControl.ProjectStopRequest => JobStates.Stopped,
                    _ => JobStates.Paused
                };
                error = nextState == JobStates.Cancelled ? null : "Execution interrupted; inspect worktree before resuming";
            }
        }
        var controlPending = LocalJobControl.IsProjectRequest(requestedState) ||
            requestedState is JobStates.Paused or JobStates.Stopped;
        if (!interrupted && controlPending &&
            (JobStates.Active.Contains(nextState) || nextState is JobStates.PrReady or JobStates.CompletedLocal))
            throw new OperationCanceledException("The current execution has an interruption request.");
        if (nextState == JobStates.Cancelled && requestedState != LocalJobControl.ProjectCancelRequest)
            throw new InvalidOperationException("The current execution has no cancellation request.");
        if (previousState == nextState && previousStage == stage && (error is null || error == previousError))
            return;
        if (expected is not null && (expected.State != previousState || expected.Stage != previousStage ||
            expected.CheckpointAt != checkpointAt))
            throw new LostExecutionLeaseException("The job stage changed since it was observed.");
        var reason = JobLifecycle.FencedTransition(previousState, nextState);
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
        RecordLifecycle(db, tx, projectId, "job", jobId, fence.Owner, previousState,
            nextState, reason, generation: fence.Generation);
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
    public bool RenewLease(Guid jobId, ExecutionFence fence, int leaseSeconds) =>
        RenewLease(jobId, fence, leaseSeconds, out _);

    internal bool RenewLease(Guid jobId, ExecutionFence fence, int leaseSeconds, out string? requestedState)
    {
        if (leaseSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        var now = DateTimeOffset.UtcNow;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var command = Command(db, tx, """
            UPDATE jobs SET lease_until=$until
            WHERE id=$id AND execution_generation=$generation AND lease_token=$token
              AND lease_owner=$owner AND lease_until>$now
              AND state IN ('preparing','building','gating','reviewing','reworking','awaiting_approval')
            RETURNING requested_state;
            """, ("$until", now.AddSeconds(leaseSeconds)), ("$id", jobId),
            ("$generation", fence.Generation), ("$token", fence.Token),
            ("$owner", fence.Owner), ("$now", now));
        requestedState = command.ExecuteScalar() as string;
        tx.Commit();
        return requestedState is not null;
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
        var job = ReadJob(db, tx, id) ?? throw new ArgumentException($"Unknown job: {id}");
        tx.Commit();
        return job;
    }

    private static FactoryJob? ReadJob(SqliteConnection db, SqliteTransaction tx, Guid id, Guid? projectId = null)
    {
        var map = Entities.Single(m => m.Type == typeof(FactoryJob));
        var scalar = Scalars(map);
        FactoryJob? job;
        using (var cmd = Command(db, tx, "SELECT " +
            string.Join(",", scalar.Select(p => Q(Column(p.Name)))) +
            " FROM jobs WHERE id=$id AND ($project IS NULL OR project_id=$project);", ("$id", id), ("$project", projectId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return null;
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

    public FactoryJob AddJob(FactoryJob job) => Change((s, db, tx) =>
    {
        SchedulingResources.Validate(job.SharedResources);
        if (HasAdoptedSpecification(db, tx, job.GoalId) ||
            (job.GoalId is { } goal && RequiresIntakePlanAdoption(db, tx, goal)))
            throw new InvalidOperationException("Add jobs through a reviewed plan revision for this goal.");
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
