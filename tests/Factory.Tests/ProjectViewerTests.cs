using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Factory;
using Microsoft.Data.Sqlite;

internal static class ProjectViewerTests
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var home = Path.Combine(Path.GetTempPath(), "factory-viewers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StateStore(home);
            var first = new ProjectInfo { Name = "First", Path = "private-host-path" };
            var second = new ProjectInfo { Name = "OtherProject" };
            var foreignGoal = new FactoryGoal { ProjectId = second.Id };
            var job = new FactoryJob { ProjectId = first.Id, HeadSha = "first-commit", LeaseToken = "private-lease" };
            var foreign = new FactoryJob { ProjectId = second.Id, HeadSha = "foreign-commit" };
            var firstUsage = new ProviderUsageEntry { ProjectId = first.Id, ProviderId = "codex", InputTokens = 7 };
            var extraUsage = new ProviderUsageEntry { ProjectId = first.Id, ProviderId = "codex" };
            job.EvidenceFile = QualityEngine.Evidence(store, job, new QualityResult
            {
                HeadSha = job.HeadSha, Findings = ["private-review-finding"],
                Checks = [new CheckReport { Name = "private-check-name", Output = "private-check-output" }]
            });
            store.Change(state =>
            {
                state.Projects.AddRange([first, second]);
                state.Goals.Add(foreignGoal);
                state.Jobs.AddRange([job, foreign]);
                state.ProviderUsages.AddRange([
                    firstUsage, extraUsage,
                    new ProviderUsageEntry { ProjectId = second.Id, ProviderId = "codex", InputTokens = 999 }
                ]);
            });

            using (var db = new SqliteConnection("Data Source=" + Path.Combine(home, "factory.sqlite3")))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    DROP TABLE viewer_project_grants;
                    DROP TABLE viewer_subjects;
                    DROP TABLE viewer_access_audit;
                    PRAGMA user_version=11;
                    """;
                cmd.ExecuteNonQuery();
            }
            store = new StateStore(home);
            var alice = store.CreateViewer("Alice");
            var bob = store.CreateViewer("Bob");
            store.SetViewerGrant(alice.SubjectId, first.Id, grant: true);
            store.SetViewerGrant(bob.SubjectId, second.Id, grant: true);

            var settings = new FactorySettings();
            settings.EnsureCredentials();
            File.WriteAllText(Path.Combine(home, "settings.json"), Json.Stringify(settings));
            using var dashboard = await RemoteRestoreTests.DashboardProcess.StartAsync(
                home, "unused-executor-test-token", "http://127.0.0.1:0", timeout.Token);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(dashboard.Address)
            };
            var firstUrl = $"/api/viewer/projects/{first.Id}";
            var secondUrl = $"/api/viewer/projects/{second.Id}";
            var summary = await Request(alice.Token, firstUrl, HttpStatusCode.OK);
            using var summaryJson = JsonDocument.Parse(summary);
            Require(summaryJson.RootElement.GetProperty("usage")[0].GetProperty("inputTokens").GetInt64() == 7);
            Require(summary.Contains(job.Id.ToString(), StringComparison.Ordinal));
            Require(!summary.Contains("private-", StringComparison.Ordinal));
            Require(!summary.Contains(foreign.Id.ToString(), StringComparison.Ordinal));
            await Request(bob.Token, secondUrl, HttpStatusCode.OK);
            await Request(bob.Token, firstUrl, HttpStatusCode.NotFound);
            await Request(alice.Token, secondUrl, HttpStatusCode.NotFound);
            await Request(alice.Token, firstUrl + $"/jobs/{foreign.Id}/quality", HttpStatusCode.NotFound);
            var quality = await Request(alice.Token, firstUrl + $"/jobs/{job.Id}/quality", HttpStatusCode.OK);
            Require(quality.Contains("changes_required", StringComparison.Ordinal));
            Require(!quality.Contains("private-", StringComparison.Ordinal));
            await Request(alice.Token, firstUrl, HttpStatusCode.Unauthorized, HttpMethod.Post);
            await Request(alice.Token, firstUrl + "/unregistered", HttpStatusCode.Unauthorized);
            await Request(alice.Token, "/api/state", HttpStatusCode.Unauthorized);
            await Request(alice.Token, "/api/worker/claim", HttpStatusCode.Unauthorized, HttpMethod.Post);
            await Request(alice.Token, "/api/executor/turn", HttpStatusCode.Unauthorized, HttpMethod.Post);
            await Request(settings.ApiToken, firstUrl, HttpStatusCode.Unauthorized);
            await Request(settings.ApiToken, "/api/state", HttpStatusCode.OK);

            UpdateFixture("UPDATE goals SET created_at='PRIVATE-FOREIGN-TIMESTAMP' WHERE id=$id;", foreignGoal.Id);
            Require(await Request(alice.Token, firstUrl, HttpStatusCode.OK) == summary);
            Require(await Request(alice.Token, firstUrl + $"/jobs/{job.Id}/quality", HttpStatusCode.OK) == quality);

            UpdateFixture("UPDATE provider_usages SET input_tokens=9223372036854775807 WHERE id=$id;", extraUsage.Id);
            using (var failure = JsonDocument.Parse(await Request(alice.Token, firstUrl, HttpStatusCode.InternalServerError)))
                Require(failure.RootElement.GetProperty("error").GetString() == "Unable to read project data.");
            UpdateFixture("UPDATE provider_usages SET input_tokens=0 WHERE id=$id;", extraUsage.Id);

            store.SetViewerGrant(alice.SubjectId, first.Id, grant: false);
            await Request(alice.Token, firstUrl, HttpStatusCode.NotFound);
            store.SetViewerGrant(alice.SubjectId, first.Id, grant: true);
            var rotated = store.SetViewerCredential(alice.SubjectId, rotate: true)!;
            await Request(alice.Token, firstUrl, HttpStatusCode.Unauthorized);
            await Request(rotated, firstUrl, HttpStatusCode.OK);
            store.SetViewerCredential(alice.SubjectId, rotate: false);
            await Request(rotated, firstUrl, HttpStatusCode.Unauthorized);

            void UpdateFixture(string sql, Guid id)
            {
                using var db = new SqliteConnection("Data Source=" + Path.Combine(home, "factory.sqlite3"));
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                Require(command.ExecuteNonQuery() == 1);
            }

            async Task<string> Request(string token, string path, HttpStatusCode status, HttpMethod? method = null)
            {
                using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add(WorkerAuthentication.WorkerIdHeader, "test-worker");
                using var response = await client.SendAsync(request, timeout.Token);
                if (response.StatusCode != status)
                    throw new InvalidOperationException($"{request.Method} {path}: expected {status}, got {response.StatusCode}");
                return await response.Content.ReadAsStringAsync(timeout.Token);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Unexpected viewer projection");
    }
}
