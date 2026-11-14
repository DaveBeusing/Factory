using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Factory;

internal sealed class RemoteProviderAgent(Uri server, string token, WorkerClaim claim, string role) : IAgentRuntime
{
    public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
        CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
        Action<AgentTokenUsage>? onUsage = null)
    {
        if (claim.ProviderLeaseId is not { } lease || claim.ProviderRoutes?.Count(r => r.Role == role) != 1 ||
            readOnly != (role != "builder"))
            throw new InvalidOperationException("Worker has no authorized provider route for the turn");
        if ((await RemoteProviderExecutor.WorkerGitAsync(directory, cancellation, "status", "--porcelain")).Length != 0)
            throw new InvalidOperationException("Trusted remote turns require a clean Git worktree");
        var commit = await RemoteProviderExecutor.WorkerGitAsync(directory, cancellation, "rev-parse", "HEAD");
        var turn = Guid.NewGuid();
        var bundle = Path.Combine(Path.GetTempPath(), $"factory-{turn:N}.bundle");
        var patch = Path.Combine(Path.GetTempPath(), $"factory-{turn:N}.patch");
        try
        {
            await RemoteProviderExecutor.WorkerGitAsync(directory, cancellation, "bundle", "create", bundle, "HEAD");
            if (new FileInfo(bundle).Length > RemoteProviderRouting.MaxBundleBytes)
                throw new InvalidOperationException("Worker Git bundle exceeds the executor size limit");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            {
                BaseAddress = server, Timeout = TimeSpan.FromMinutes(10),
                MaxResponseContentBufferSize = 3 * RemoteProviderRouting.MaxPatchBytes
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            http.DefaultRequestHeaders.Add(WorkerAuthentication.WorkerIdHeader, claim.WorkerId);
            using var response = await http.PostAsJsonAsync("api/worker/turn", new RemoteProviderTurnRequest
            {
                WorkerId = claim.WorkerId, JobId = claim.Job.Id, LeaseId = lease, TurnId = turn,
                ExecutionGeneration = claim.Job.ExecutionGeneration, LeaseToken = claim.Job.LeaseToken,
                Role = role, Commit = commit,
                Bundle = await File.ReadAllBytesAsync(bundle, cancellation)
            }, Json.Options, cancellation);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<RemoteExecutorReceipt>(Json.Options, cancellation)
                ?? throw new InvalidOperationException("Controller returned no trusted turn receipt");
            var route = claim.ProviderRoutes!.Single(r => r.Role == role);
            if (result.LeaseId != lease || result.TurnId != turn || result.InputCommit != commit ||
                result.ProviderId != route.ProviderId || result.Role != role || result.ObservedModel != route.Model)
                throw new InvalidOperationException("Controller receipt does not match the worker turn");
            if (!readOnly && result.Success)
            {
                if (result.Patch.Length > 0)
                {
                    await File.WriteAllBytesAsync(patch, result.Patch, cancellation);
                    await RemoteProviderExecutor.WorkerGitAsync(directory, cancellation, "apply", "--index", "--binary", patch);
                }
                if (await RemoteProviderExecutor.WorkerGitAsync(directory, cancellation, "write-tree") != result.OutputTree)
                    throw new InvalidOperationException("Executor patch does not reproduce the verified tree");
            }
            var session = result.TurnId.ToString("D");
            onSession?.Invoke(session);
            return new AgentRunResult
            {
                SessionId = session,
                Success = result.Success, Error = result.Success ? "" : "Trusted provider turn failed",
                ObservedModel = result.ObservedModel, FinalText = result.FinalText,
                FinalTextIsFallback = result.FinalTextIsFallback, InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens, UsageReported = result.FinalUsage
            };
        }
        finally
        {
            File.Delete(bundle);
            File.Delete(patch);
        }
    }
}
