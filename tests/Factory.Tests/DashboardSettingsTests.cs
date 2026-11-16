using Factory;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DashboardSettingsTests
{
    internal static async Task CommitAsync()
    {
        var home = Directory.CreateTempSubdirectory("factory-settings-");
        try
        {
            var store = new StateStore(home.FullName);
            var path = Path.Combine(home.FullName, "settings.json");
            var settings = new FactorySettings { MaxParallel = 2, MaxAgentSessions = 3, Model = "fixture-model" };
            settings.EnsureCredentials();
            SettingsFile.Save(path, settings);
            var original = settings.RuntimeRevision;
            var originalFile = File.ReadAllText(path);

            try
            {
                settings.CommitRuntime(current => current with { MaxAgentSessions = 8 }, candidate =>
                {
                    Require(candidate.MaxAgentSessions == 8, "The candidate was not prepared before persistence.");
                    Require(settings.RuntimeRevision == original, "Candidate settings leaked before persistence.");
                    throw new IOException("Injected settings write failure.");
                }, _ => throw new Exception("Failed save notified live consumers."));
                throw new Exception("Expected injected settings write failure.");
            }
            catch (IOException ex) when (ex.Message == "Injected settings write failure.") { }
            Require(File.ReadAllText(path) == originalFile && settings.RuntimeRevision == original,
                "Write failure changed persisted or live settings.");

            var backup = path + ".fixture-backup";
            File.Move(path, backup);
            Directory.CreateDirectory(path);
            try
            {
                DashboardSettings.Save(store, settings, new SettingsInput { MaxAgentSessions = 9 }, NullLogger.Instance);
                throw new Exception("Expected settings replacement to fail for a directory destination.");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Require(settings.RuntimeRevision == original && File.ReadAllText(backup) == originalFile &&
                !Directory.EnumerateFiles(home.FullName, "settings.json.*.tmp").Any(),
                "Failed replacement changed live settings, altered the previous file or leaked a temporary file.");
            Require(store.Read().Events.All(item => item.Type != "settings_updated"), "Failed save was audited as committed.");
            Directory.Delete(path);
            File.Move(backup, path);

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new SettingsInput { MaxParallel = 5, MaxAgentSessions = 7, GoalReservationTokens = 2048 };
            var second = new SettingsInput { MaxParallel = 8, MaxAgentSessions = 4, MaxJobReservationTokens = 32768 };
            async Task<RuntimeSettingsRevision> SaveAsync(SettingsInput input)
            {
                await start.Task;
                var saved = DashboardSettings.Save(store, settings, input, NullLogger.Instance);
                Require(saved.AuditWarning is null, "A healthy audit unexpectedly returned a warning.");
                return saved.Revision;
            }
            var firstSave = Task.Run(() => SaveAsync(first));
            var secondSave = Task.Run(() => SaveAsync(second));
            start.SetResult();
            var results = await Task.WhenAll(firstSave, secondSave);
            var saved = FactorySettings.Load(path);
            Require(saved.RuntimeRevision == settings.RuntimeRevision, "Persisted and live settings revisions differ.");
            Require(saved.RuntimeRevision == results[0] || saved.RuntimeRevision == results[1], "Settings mixed concurrent revisions.");
            Require(results[0].MaxParallel == 5 && results[0].MaxAgentSessions == 7 &&
                results[1].MaxParallel == 8 && results[1].MaxAgentSessions == 4, "A concurrent save returned mixed fields.");
            Require(saved.GoalReservationTokens == 2048 && saved.MaxJobReservationTokens == 32768,
                "Concurrent partial updates lost a field.");
            Require(saved.Model == settings.Model && saved.ApiToken == settings.ApiToken &&
                saved.WorkerTokenKey == settings.WorkerTokenKey, "Saving runtime settings changed unrelated settings.");
            Require(store.Read().Events.Count(item => item.Type == "settings_updated") == 2,
                "Each successful revision must emit one audit event.");

            using var db = new SqliteConnection($"Data Source={Path.Combine(home.FullName, "factory.sqlite3")};Pooling=False");
            db.Open();
            using var trigger = db.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER fail_settings_audit BEFORE INSERT ON events
                WHEN NEW.type = 'settings_updated' BEGIN SELECT RAISE(ABORT, 'Fixture audit failure'); END;
                """;
            trigger.ExecuteNonQuery();
            var audited = DashboardSettings.Save(store, settings, new SettingsInput { MaxAgentSessions = 10 }, NullLogger.Instance);
            Require(audited.AuditWarning?.Contains("saved and applied", StringComparison.Ordinal) == true,
                "A failed audit must report the committed result with an explicit warning.");
            Require(settings.MaxAgentSessions == 10 && settings.RuntimeRevision == audited.Revision &&
                FactorySettings.Load(path).RuntimeRevision == audited.Revision,
                "An audit failure misreported or rolled back persisted runtime settings.");
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
