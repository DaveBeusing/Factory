using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            Require(ReadSchema(freshState) == 22, "Fresh installation did not create schema 22");
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
            Console.WriteLine("PASS Windows package upgrade from schema 8 to 22 with project, usage and settings preserved");

            Require(ReadSchema(backup) == 8, "Schema of the original backup changed during upgrade");
            var restored = Directory.CreateDirectory(Path.Combine(scratch.FullName, "restored")).FullName;
            foreach (var name in new[] { "factory.sqlite3", "settings.json" })
                File.Copy(Path.Combine(backup, name), Path.Combine(restored, name));
            Require(ReadSchema(restored) == 8, "Restore did not start with the schema 8 backup");
            await RunFactoryAsync(executable, restored, "providers");
            VerifyUpgradedState(restored, project.Id, settings);
            Require(ReadSchema(backup) == 8, "Restoring the backup modified its original schema");
            Console.WriteLine("PASS Windows package restore and re-upgrade from the preserved schema 8 backup");
            await VerifyOperatorMigrationAsync(executable, restored, project, settings);
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

    private static async Task VerifyOperatorMigrationAsync(string executable, string home, ProjectInfo project, byte[] oldSettings)
    {
        const string listen = "http://127.0.0.1:0";
        const string projectIndex = "api/viewer/projects";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        var original = JsonSerializer.Deserialize<FactorySettings>(oldSettings, Json.Options)!;
        using (var database = OpenDatabase(home))
        using (var query = database.CreateCommand())
        {
            query.CommandText = "SELECT COUNT(*) FROM viewer_subjects;";
            Require(Convert.ToInt64(query.ExecuteScalar()) == 0, "Migration granted access to an unexpected project identity");
        }
        var foreign = new ProjectInfo { Name = "Unassigned migration fixture" };
        new StateStore(home).Change(state => state.Projects.Add(foreign));
        string viewerToken;
        Guid subject;
        string initialGrants;
        using (var dashboard = await RemoteRestoreTests.DashboardProcess.StartAsync(home, "", listen, ct, executable))
        using (var client = OpenClient(dashboard.Address))
        {
            await Check(client, original.ApiToken, "api/state", HttpStatusCode.OK);
            await Check(client, original.ApiToken, projectIndex, HttpStatusCode.Unauthorized);
            using var created = JsonDocument.Parse(await RunFactoryAsync(executable, home, "viewer", "create", "--name", "LT-04 migration fixture"));
            viewerToken = created.RootElement.GetProperty("token").GetString()!;
            subject = created.RootElement.GetProperty("subjectId").GetGuid();
            Require(await Check(client, viewerToken, projectIndex, HttpStatusCode.OK) == "[]",
                "A new project identity started with grants");
            await RunFactoryAsync(executable, home, "viewer", "grant-planner", "--subject", subject.ToString(), "--project", project.Name);
            initialGrants = await Check(client, viewerToken, projectIndex, HttpStatusCode.OK);
            using var grants = JsonDocument.Parse(initialGrants);
            var allowed = grants.RootElement.EnumerateArray().Single();
            Require(allowed.GetProperty("id").GetGuid() == project.Id && allowed.GetProperty("canPlan").GetBoolean(),
                "The explicit Planner grant was not applied");
            await Check(client, viewerToken, "api/state", HttpStatusCode.Unauthorized);
            await Check(client, viewerToken, projectIndex + "/" + foreign.Id, HttpStatusCode.NotFound);
        }
        Console.WriteLine("PASS administrator-only migration preserves admin access and requires explicit project grants");

        var settingsPath = Path.Combine(home, "settings.json");
        var recoverySettings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        Require(recoverySettings.Remove("apiToken"), "Missing administrator credential in recovery fixture");
        File.WriteAllText(settingsPath, recoverySettings.ToJsonString());
        await RunFactoryAsync(executable, home, "providers");
        var recovered = JsonSerializer.Deserialize<FactorySettings>(File.ReadAllText(settingsPath), Json.Options)!;
        Require(recovered.ApiToken.Length > 0 && recovered.ApiToken != original.ApiToken &&
            recovered.WorkerTokenKey == original.WorkerTokenKey, "Local recovery did not replace only the administrator credential");

        using (var dashboard = await RemoteRestoreTests.DashboardProcess.StartAsync(home, "", listen, ct, executable))
        using (var client = OpenClient(dashboard.Address))
        {
            await Check(client, original.ApiToken, "api/state", HttpStatusCode.Unauthorized);
            await Check(client, recovered.ApiToken, "api/state", HttpStatusCode.OK);
            Require(await Check(client, viewerToken, projectIndex, HttpStatusCode.OK) == initialGrants,
                "Administrator recovery changed project grants");
            Console.WriteLine("PASS offline administrator recovery replaces the lost token and preserves project grants");

            using var rotated = JsonDocument.Parse(await RunFactoryAsync(executable, home, "viewer", "rotate", "--subject", subject.ToString()));
            var newToken = rotated.RootElement.GetProperty("token").GetString()!;
            Require(rotated.RootElement.GetProperty("subjectId").GetGuid() == subject && newToken != viewerToken,
                "Viewer rotation did not preserve identity and replace the credential");
            await Check(client, viewerToken, projectIndex, HttpStatusCode.Unauthorized);
            Require(await Check(client, newToken, projectIndex, HttpStatusCode.OK) == initialGrants,
                "Viewer rotation changed project grants");
            await Check(client, newToken, projectIndex + "/" + foreign.Id, HttpStatusCode.NotFound);
            await Check(client, newToken, "api/state", HttpStatusCode.Unauthorized);
        }
        Console.WriteLine("PASS live project token rotation rejects the old token and preserves scoped access");

        static HttpClient OpenClient(string address) => new(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(address) };

        async Task<string> Check(HttpClient client, string token, string route, HttpStatusCode expected)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, ct);
            Require(response.StatusCode == expected, $"{route} returned {response.StatusCode} instead of {expected}");
            return await response.Content.ReadAsStringAsync(ct);
        }
    }

    private static void VerifyUpgradedState(string directory, Guid projectId, byte[] settings)
    {
        // Read the pragma before using StateStore so the verifier cannot perform the upgrade itself.
        Require(ReadSchema(directory) == 22, "The packaged executable did not migrate to schema 22");
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
