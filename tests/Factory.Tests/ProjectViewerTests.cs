using System.Net;
using System.Net.Http.Headers;
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
            var job = new FactoryJob { ProjectId = first.Id, HeadSha = "first-commit", LeaseToken = "private-lease" };
            var foreign = new FactoryJob { ProjectId = second.Id, HeadSha = "foreign-commit" };
            job.EvidenceFile = QualityEngine.Evidence(store, job, new QualityResult
            {
                HeadSha = job.HeadSha, Findings = ["private-review-finding"],
                Checks = [new CheckReport { Name = "private-check-name", Output = "private-check-output" }]
            });
            store.Change(state =>
            {
                state.Projects.AddRange([first, second]);
                state.Jobs.AddRange([job, foreign]);
                state.ProviderUsages.AddRange([
                    new ProviderUsageEntry { ProjectId = first.Id, ProviderId = "codex", InputTokens = 7 },
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
            using var summaryJson = System.Text.Json.JsonDocument.Parse(summary);
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

            store.SetViewerGrant(alice.SubjectId, first.Id, grant: false);
            await Request(alice.Token, firstUrl, HttpStatusCode.NotFound);
            store.SetViewerGrant(alice.SubjectId, first.Id, grant: true);
            var rotated = store.SetViewerCredential(alice.SubjectId, rotate: true)!;
            await Request(alice.Token, firstUrl, HttpStatusCode.Unauthorized);
            await Request(rotated, firstUrl, HttpStatusCode.OK);
            store.SetViewerCredential(alice.SubjectId, rotate: false);
            await Request(rotated, firstUrl, HttpStatusCode.Unauthorized);

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
