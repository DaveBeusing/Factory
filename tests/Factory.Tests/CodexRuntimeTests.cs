using System.Text.Json;
using Factory;

internal static class CodexRuntimeTests
{
    internal static void ExecEvidence()
    {
        const string assistant = """{"type":"item.completed","item":{"type":"agent_message","text":"PASS"}}""";
        const string usage = """{"type":"turn.completed","usage":{"input_tokens":7,"output_tokens":4}}""";
        var missing = new CodexExecTurn();
        missing.Observe(assistant);
        Require(!missing.Finish(true, true).Success, "Clean exit without terminal event was accepted");

        var partial = new CodexExecTurn();
        partial.Observe(assistant);
        partial.Observe("""{"type":"turn.completed","usage":{"input_tokens":4}}""");
        var partialResult = partial.Finish(true, true);
        Require(partialResult.Success && !partialResult.UsageReported, "Partial usage must stay unknown");

        var reports = 0;
        var completed = new CodexExecTurn(onUsage: observed => { reports++; Require(observed.Total == 11, "Unexpected usage"); });
        completed.Observe(assistant);
        completed.Observe(usage);
        Require(completed.Finish(true, true) is { Success: true, UsageReported: true }, "Valid result rejected");
        completed.Observe("""{"type":"turn.failed"}""");
        Require(completed.Finish(true, true) is { Success: false, UsageReported: false, InputTokens: 7, OutputTokens: 4 } &&
            reports == 1, "Contradictory terminal evidence was accepted as final usage");

        var malformed = new CodexExecTurn();
        malformed.Observe("not a JSON event");
        malformed.Observe(assistant);
        malformed.Observe(usage);
        Require(malformed.Finish(true, true) is { Success: false, UsageReported: false },
            "Malformed protocol was accepted as final usage");

        var noMessage = new CodexExecTurn();
        noMessage.Observe(usage);
        Require(!noMessage.Finish(true, true).Success, "Missing structured agent output was accepted");
    }

    internal static async Task AppServerEvidenceAsync()
    {
        var runtime = new AppServerAgentRuntime("fixture", verifyModel: true);
        var directory = Path.GetFullPath(".");
        const string ready = """{"id":1,"result":{}}""";
        const string turnStarted = """{"id":3,"result":{"turn":{"id":"turn"}}}""";
        const string completed = """{"method":"turn/completed","params":{"threadId":"thread","turn":{"id":"turn","status":"completed"}}}""";
        const string message = """{"method":"item/completed","params":{"threadId":"thread","turnId":"turn","item":{"type":"agentMessage","text":"current"}}}""";
        const string invalidTurn = """{"method":"turn/completed","params":{"threadId":"thread","turn":{"id":"prior","status":"failed"}}}""";
        const string invalidThread = """{"method":"turn/completed","params":{"threadId":"another","turn":{"id":"turn","status":"failed"}}}""";
        const string missingTurn = """{"method":"item/completed","params":{"threadId":"thread","item":{"type":"agentMessage","text":"unknown"}}}""";
        const string usage = """
        {"method":"thread/tokenUsage/updated","params":{"threadId":"thread","turnId":"turn","tokenUsage":{"total":{"inputTokens":57,"outputTokens":42},"last":{"inputTokens":7,"outputTokens":2}}}}
        """;
        const string approval = """{"method":"item/commandExecution/requestApproval","id":"pending","params":{}}""";

        string Thread(string cwd, string access, string approvalPolicy = "never", string provider = "provider") =>
            JsonSerializer.Serialize(new
            {
                id = 2, result = new
                {
                    thread = new { id = "thread" }, cwd, model = "fixture", modelProvider = provider, approvalPolicy,
                    sandbox = new { type = access, networkAccess = false }
                }
            });

        async Task<(AgentRunResult Result, string Transcript)> Run(string[] frames, string previous = "", bool readOnly = false)
        {
            using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', frames) + "\n"));
            using var output = new StringWriter();
            var result = await runtime.RunProtocolAsync(input, output, directory, "fixture task", previous,
                CancellationToken.None, readOnly: readOnly);
            return (result, output.ToString());
        }

        var (first, transcript) = await Run([
            ready, Thread(directory, "workspaceWrite"), invalidTurn, invalidThread, usage, message,
            approval, turnStarted, missingTurn, invalidTurn, completed
        ], previous: "legacy");
        Require(first is { Success: true, FinalText: "current", InputTokens: 7, OutputTokens: 2, UsageReported: true },
            "Early or unrelated turn evidence was attributed incorrectly");
        Require(transcript.Contains("thread/start") && transcript.Contains("decline") &&
            !transcript.Contains("thread/resume"), "Unknown binding should start fresh and decline approvals");

        var (resumed, resumeRequest) = await Run([ready, Thread(directory, "workspaceWrite"), turnStarted, message, completed], "thread");
        Require(resumed.Success && resumeRequest.Contains("thread/resume"), "Matching builder session did not resume");
        var (wrongProvider, providerRequest) = await Run([ready, Thread(directory, "workspaceWrite", provider: "another")], "thread");
        Require(!wrongProvider.Success && providerRequest.Contains("thread/resume") &&
            !providerRequest.Contains("turn/start"), "Changed provider was allowed on the resumed turn");

        await Run([ready, Thread(directory, "workspaceWrite"), turnStarted, message, completed]);
        var (review, reviewRequest) = await Run([ready, Thread(directory, "readOnly"), turnStarted, message, completed],
            "thread", readOnly: true);
        Require(review.Success && reviewRequest.Contains("thread/start") &&
            !reviewRequest.Contains("thread/resume"), "Read-only review resumed a builder session");

        var (wrongDirectory, wrongDirectoryRequest) = await Run([ready, Thread(Path.Combine(directory, "other"), "workspaceWrite")], "thread");
        Require(!wrongDirectory.Success && !wrongDirectoryRequest.Contains("turn/start"),
            "Changed working directory was allowed");

        try
        {
            await Run([ready, Thread(directory, "workspaceWrite"), """{"id":3,"result":{"turn":{}}}""", completed]);
            throw new Exception("Missing start turn identity accepted");
        }
        catch (InvalidDataException) { }

        try
        {
            await Run([new string('x', 2_000_001)]);
            throw new Exception("Oversized app-server event accepted");
        }
        catch (InvalidDataException ex)
        {
            Require(ex.Message.Contains("size limit"), "Oversized event must be rejected before parsing");
        }
    }

    internal static async Task PlannerEvidenceAsync()
    {
        const string plan = """
        {"contractVersion":1,"agentPolicyRevision":"wrong","packages":[{"title":"Generated","description":"Fixture proposal","files":["README.md"],"acceptanceEvidence":[{"criterion":"Verified","check":"fixture"}]}]}
        """;
        var project = new ProjectInfo { Checks = [new CheckCommand { Name = "fixture" }] };
        var goal = new FactoryGoal { Objective = "fixture" };
        var preflight = new WorkPackagePreflightReport();
        async Task<FactoryPlan> Generate(string text, bool fallback = false) =>
            await Planning.GenerateDraftAsync(project, goal, preflight,
                new PlanResultRuntime(new() { Success = true, FinalText = text, FinalTextIsFallback = fallback }),
                CancellationToken.None, null);

        Require((await Generate(plan)).AgentPolicyRevision == AgentPolicies.Revision,
            "Planner was allowed to assert its own policy revision");
        await Reject(plan, fallback: true);
        await Reject("diagnostics before a JSON object " + plan, fallback: false);

        async Task Reject(string text, bool fallback)
        {
            try { await Generate(text, fallback); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Invalid planner output was accepted");
        }
    }

    private sealed class PlanResultRuntime(AgentRunResult result) : IAgentRuntime
    {
        public Task<AgentRunResult> RunAsync(string directory, string prompt, string previousSession,
            CancellationToken cancellation, Action<string>? onSession = null, bool readOnly = false,
            Action<AgentTokenUsage>? onUsage = null)
        {
            Require(readOnly && previousSession.Length == 0, "Planner session must start fresh and read-only");
            return Task.FromResult(result);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
