using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Factory;
using Microsoft.Data.Sqlite;

internal static class WindowsPackageValidation
{
    public static async Task RunAsync(string packageDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This check requires Windows");

        var scratch = Directory.CreateTempSubdirectory("factory-windows-package-");
        try
        {
            var archive = Path.Combine(scratch.FullName, "Factory.zip");
            var installed = Path.Combine(scratch.FullName, "installed");
            ZipFile.CreateFromDirectory(packageDirectory, archive);
            ZipFile.ExtractToDirectory(archive, installed);
            var executable = Path.Combine(installed, "Factory.exe");
            var freshState = Path.Combine(scratch.FullName, "fresh-state");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(installed, "build-manifest.json")));
            var version = await RunFactoryAsync(executable, freshState, "version");
            Require(version == manifest.RootElement.GetProperty("informationalVersion").GetString(), "Package version mismatch");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)));
            Require(string.Equals(hash, manifest.RootElement.GetProperty("executableSha256").GetString(),
                StringComparison.OrdinalIgnoreCase), "Package checksum mismatch");
            Require(File.Exists(Path.Combine(installed, "providers", "codex", "skills", "factory-plan", "SKILL.md")) &&
                File.Exists(Path.Combine(installed, "providers", "codex", "agents", "builder.toml")), "Packaged Codex assets missing");
            Require(!Directory.Exists(Path.Combine(installed, "skills")) &&
                !Directory.Exists(Path.Combine(installed, "agents")), "Codex assets must be packaged under their provider");
            var profile = Directory.CreateDirectory(Path.Combine(scratch.FullName, "codex-profile")).FullName;
            CodexAssets.Install(installed, profile, overwrite: false);
            var installedSkill = Path.Combine(profile, ".agents", "skills", "factory-plan", "SKILL.md");
            var installedAgent = Path.Combine(profile, ".codex", "agents", "factory-builder.toml");
            Require(File.Exists(installedSkill) && File.Exists(installedAgent), "Codex installer did not find packaged assets");
            File.WriteAllText(installedAgent, "local customization");
            CodexAssets.Install(installed, profile, overwrite: false);
            Require(File.ReadAllText(installedAgent) == "local customization", "Codex installer overwrote a local customization");

            var repository = Directory.CreateDirectory(Path.Combine(scratch.FullName, "repository")).FullName;
            GitOperations.Git(repository, "init", "-b", "master");
            File.WriteAllText(Path.Combine(repository, "README.md"), "# Preview validation fixture\n");
            GitOperations.Git(repository, "add", "README.md");
            GitOperations.Git(repository, "-c", "user.name=Factory Fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.Combine(scratch.FullName, "no-hooks"),
                "commit", "-m", "Create fixture");
            await RunFactoryAsync(executable, freshState, "init", repository, "--name", "Preview fixture");
            Require(ReadSchema(freshState) == 18, "Fresh installation did not create schema 18");
            var settings = File.ReadAllBytes(Path.Combine(freshState, "settings.json"));
            var store = new StateStore(freshState);
            var project = store.Read().Projects.Single();
            store.Change(state =>
            {
                var turn = ProviderBudgetLedger.Begin(state, new FactoryJob { Id = Guid.Empty, ProjectId = project.Id },
                    new ProviderProfile(), "analysis", "fixture", 512);
                ProviderBudgetLedger.Record(state, turn.Id, new(8, 4));
                ProviderBudgetLedger.Finish(state, turn.Id, true);
            });
            Console.WriteLine($"PASS fresh Windows package installation ({version})");

            // Schema 9 adds only reported_cost_micros. Recreate the exact schema 8 delta in the synthetic state.
            using (var database = OpenDatabase(freshState))
            using (var command = database.CreateCommand())
            {
                command.CommandText = """
                    DROP TABLE budget_reconciler_project_grants;
                    DROP TABLE scoped_project_reviews;
                    DROP TABLE project_goal_budget_audit;
                    DROP TABLE project_budget_audit;
                    DROP TABLE operator_project_grants;
                    DROP TABLE project_operation_audit;
                    DROP TABLE goal_plan_revisions;
                    DROP TABLE planner_project_grants;
                    DROP TABLE project_planning_audit;
                    DROP TABLE viewer_project_grants;
                    DROP TABLE viewer_subjects;
                    DROP TABLE viewer_access_audit;
                    ALTER TABLE provider_usages DROP COLUMN reported_cost_micros;
                    PRAGMA user_version=8;
                    """;
                command.ExecuteNonQuery();
            }
            Require(ReadSchema(freshState) == 8, "Expected a schema 8 input fixture");

            var backup = Directory.CreateDirectory(Path.Combine(scratch.FullName, "backup")).FullName;
            using (var source = OpenDatabase(freshState))
            using (var destination = OpenDatabase(backup, SqliteOpenMode.ReadWriteCreate))
                source.BackupDatabase(destination);
            File.WriteAllBytes(Path.Combine(backup, "settings.json"), settings);

            await RunFactoryAsync(executable, freshState, "providers");
            VerifyUpgradedState(freshState, project.Id, settings);
            Console.WriteLine("PASS Windows package upgrade from schema 8 to 18 with project, usage and settings preserved");

            Require(ReadSchema(backup) == 8, "Schema of the original backup changed during upgrade");
            var restored = Directory.CreateDirectory(Path.Combine(scratch.FullName, "restored")).FullName;
            foreach (var name in new[] { "factory.sqlite3", "settings.json" })
                File.Copy(Path.Combine(backup, name), Path.Combine(restored, name));
            Require(ReadSchema(restored) == 8, "Restore did not start with the schema 8 backup");
            await RunFactoryAsync(executable, restored, "providers");
            VerifyUpgradedState(restored, project.Id, settings);
            Require(ReadSchema(backup) == 8, "Restoring the backup modified its original schema");
            Console.WriteLine("PASS Windows package restore and re-upgrade from the preserved schema 8 backup");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // Git writes read-only object files on Windows.
            foreach (var file in scratch.EnumerateFiles("*", SearchOption.AllDirectories))
                if (file.IsReadOnly) file.IsReadOnly = false;
            scratch.Delete(recursive: true);
        }
    }

    private static void VerifyUpgradedState(string directory, Guid projectId, byte[] settings)
    {
        // Read the pragma before using StateStore so the verifier cannot perform the upgrade itself.
        Require(ReadSchema(directory) == 18, "The packaged executable did not migrate to schema 18");
        var state = new StateStore(directory).Read();
        Require(state.Projects.Single().Id == projectId, "Project identity changed");
        var usage = state.ProviderUsages.Single();
        Require(usage.ProjectId == projectId && usage.InputTokens == 8 && usage.OutputTokens == 4 &&
            usage.FinalUsageReported && usage.ReportedCostMicros is null, "Provider usage or historical cost changed");
        Require(File.ReadAllBytes(Path.Combine(directory, "settings.json")).SequenceEqual(settings), "Settings changed");
    }

    private static int ReadSchema(string directory)
    {
        using var database = OpenDatabase(directory, SqliteOpenMode.ReadOnly);
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static SqliteConnection OpenDatabase(string directory, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "factory.sqlite3"), Mode = mode, Pooling = false
        }.ToString());
        database.Open();
        return database;
    }

    private static async Task<string> RunFactoryAsync(string executable, string state, params string[] arguments)
    {
        var start = Commands.StartInfo(executable, arguments, Path.GetDirectoryName(executable)!);
        start.Environment["FACTORY_HOME"] = state;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the installed Factory.exe");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        Require(process.ExitCode == 0, $"Factory {arguments[0]} failed ({process.ExitCode}): {await error}");
        return (await output).Trim();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
