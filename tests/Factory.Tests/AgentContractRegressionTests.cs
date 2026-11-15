using System.Text.Json;
using Factory;

internal static class AgentContractRegressionTests
{
    internal static async Task ResizeLimiter()
    {
        var settings = new FactorySettings { MaxAgentSessions = 2 };
        var first = new ControlledRuntime();
        var second = new ControlledRuntime();
        var third = new ControlledRuntime();
        var fourth = new ControlledRuntime();
        var a = Run(first);
        var b = Run(second);
        await Task.WhenAll(first.Started, second.Started).WaitAsync(TimeSpan.FromSeconds(5));
        settings.MaxAgentSessions = 1;
        SlottedAgentRuntime.NotifyLimitChanged(settings);
        var c = Run(third);
        Check(!third.Started.IsCompleted, "A new runtime bypassed the lowered controller limit.");
        using var cancelled = new CancellationTokenSource();
        var d = Run(fourth, cancelled.Token);
        cancelled.Cancel();
        try
        {
            await d;
            throw new InvalidOperationException("Cancelled agent waiter entered.");
        }
        catch (OperationCanceledException) { }
        first.Complete();
        await a;
        Check(!third.Started.IsCompleted && !fourth.Started.IsCompleted, "A waiter entered before enough turns completed.");
        settings.MaxAgentSessions = 2;
        SlottedAgentRuntime.NotifyLimitChanged(settings);
        await third.Started.WaitAsync(TimeSpan.FromSeconds(5));
        second.Complete();
        third.Complete();
        await Task.WhenAll(b, c).WaitAsync(TimeSpan.FromSeconds(5));

        Task<AgentRunResult> Run(ControlledRuntime runtime, CancellationToken token = default) =>
            new SlottedAgentRuntime(runtime, settings).RunAsync(".", "fixture", "", token);
    }

    internal static async Task EvaluatePolicies()
    {
        var home = Directory.CreateTempSubdirectory("factory-agent-eval-");
        try
        {
            var store = new StateStore(home.FullName);
            var suite = Path.Combine(home.FullName, "suite.json");
            EvaluationCase[] cases =
            [
                new() { Name = "Approval", Kind = "approval_default", Expected = "human" },
                new() { Name = "Self approval", Kind = "independent_approval", Input = "self", Expected = "deny" },
                new() { Name = "Missing approval", Kind = "independent_approval", Input = "missing", Expected = "hold" },
                new() { Name = "Independent approval", Kind = "independent_approval", Input = "independent", Expected = "allow" },
                new() { Name = "Planner output", Kind = "planning_contract", Input = "{", Expected = "invalid" },
                new() { Name = "Review verdict", Kind = "review_verdict", Input = "VERDICT: PASS with caveats", Expected = "CHANGES" },
                new() { Name = "Review mode", Kind = "role_mode", Input = "review:security", Expected = "read-only" },
                new() { Name = "Offline live request", Kind = "agent_answer_contains", Expected = "match" }
            ];
            await File.WriteAllTextAsync(suite, Json.Stringify(cases));
            var result = await Evaluations.RunAsync(store, suite);
            Check(result.Passed == cases.Length - 1, string.Join(", ", result.Failures));
            var evidenceFile = Path.Combine(home.FullName, "evaluation-evidence", result.Id.ToString("N") + ".json");
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidenceFile));
            Check(evidence.RootElement[0].GetProperty("policyRevision").GetString() == AgentPolicies.Revision &&
                evidence.RootElement[cases.Length - 1].GetProperty("outcome").GetString() == "not_run",
                "Evaluation evidence lacks revision or skipped status.");
            try
            {
                await Evaluations.RunAsync(store, suite, ct: new CancellationToken(canceled: true));
                throw new InvalidOperationException("A cancelled suite returned a passing or failing result.");
            }
            catch (OperationCanceledException) { }
            Check(store.Read().Evaluations.Count == 1, "Cancelled evaluations were recorded.");
        }
        finally { try { home.Delete(true); } catch (IOException) { } }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ControlledRuntime : IAgentRuntime
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Started => _started.Task;
        internal void Complete() => _complete.TrySetResult();

        public async Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
            CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
            Action<AgentTokenUsage>? onUsage = null)
        {
            _started.TrySetResult();
            await _complete.Task.WaitAsync(cancellation);
            return new() { Success = true };
        }
    }
}
