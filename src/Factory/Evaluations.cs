using System.Text.Json;

namespace Factory;

public sealed class EvaluationCase
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Input { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Role { get; set; } = "analysis";
}

internal sealed record EvaluationCaseEvidence(string Name, string Role, string? InputCommit, string PolicyRevision,
    string? Verdict, string Outcome, string? Diagnostic);

public static class Evaluations
{
    public static Task<EvaluationResult> RunAsync(StateStore store, string suiteFile,
        IAgentRuntime? agent = null, string? directory = null, CancellationToken ct = default) =>
        RunCoreAsync(store, suiteFile, agent is null ? null : _ => agent, directory, ct);

    internal static Task<EvaluationResult> RunLiveAsync(StateStore store, string suiteFile,
        ProjectInfo project, FactorySettings settings, CancellationToken ct = default) =>
        RunCoreAsync(store, suiteFile, role =>
        {
            var state = store.Read();
            var provider = ProviderRouter.Resolve(settings, state, project.Id, role);
            var route = ProviderRouter.Select(settings, state, project.Id, role, "medium");
            return new ProjectScopedAgentRuntime(route.Runtime, store, project.Id, null,
                provider, role, route.Model, settings.GoalReservationTokens);
        }, project.Path, ct);

    private static async Task<EvaluationResult> RunCoreAsync(StateStore store, string suiteFile,
        Func<string, IAgentRuntime>? runtime, string? directory, CancellationToken ct)
    {
        var cases = JsonSerializer.Deserialize<List<EvaluationCase>>(File.ReadAllText(suiteFile), Json.Options)
            ?? throw new InvalidOperationException("Empty evaluation suite");
        var result = new EvaluationResult { Suite = Path.GetFileName(suiteFile), Total = cases.Count };
        var evidence = new List<EvaluationCaseEvidence>();
        var commit = runtime is null || directory is null ? null :
            WorkPackagePreflight.MasterCommit(new ProjectInfo { Path = directory });
        if (runtime is not null && directory is not null)
            Planning.RequirePlanningCheckout(new ProjectInfo { Path = directory }, commit!);
        foreach (var test in cases)
        {
            ct.ThrowIfCancellationRequested();
            var passed = false;
            string? diagnostic = null;
            string? verdict = null;
            var outcome = "failed";
            try
            {
                if (test.Kind == "agent_answer_contains")
                {
                    if (runtime is null || directory is null)
                    {
                        outcome = "not_run";
                        diagnostic = "Live agent evaluation requires an enabled project runtime.";
                    }
                    else
                    {
                        var policy = AgentPolicies.For(test.Role);
                        if (!policy.ReadOnly) throw new InvalidOperationException("Live evaluations are read-only.");
                        var answer = await runtime(test.Role).RunAsync(directory,
                            policy.Render("Answer the evaluation question using the permitted sources.", new { test.Input }),
                            "", ct, readOnly: true);
                        result.TokensIn = checked(result.TokensIn + answer.InputTokens);
                        result.TokensOut = checked(result.TokensOut + answer.OutputTokens);
                        if (!answer.Success || answer.FinalTextIsFallback || string.IsNullOrWhiteSpace(answer.FinalText))
                        {
                            outcome = "invalid_output";
                            diagnostic = "Agent returned no successful structured answer.";
                        }
                        else
                        {
                            passed = test.Expected.Length > 0 &&
                                answer.FinalText.Contains(test.Expected, StringComparison.OrdinalIgnoreCase);
                            verdict = passed ? "matches" : "does_not_match";
                        }
                    }
                }
                else passed = test.Kind switch
                {
                    "path_normalization" => GitOperations.SamePath(test.Input, test.Expected),
                    "approval_default" => test.Expected == "human" && DefaultApprovalRequiresHuman(),
                    "independent_approval" => IndependentApprovalOutcome(test.Input) == test.Expected,
                    "dependency_cycle" => CheckCycle(test.Input) == bool.Parse(test.Expected),
                    "risk_priority" => int.Parse(test.Input) is >= 0 and <= 100,
                    "planning_contract" => ValidatePlan(test.Input) == test.Expected,
                    "review_verdict" => QualityEngine.ReviewVerdict(new() { Success = true, FinalText = test.Input }) == test.Expected,
                    "role_mode" => (AgentPolicies.For(test.Input).ReadOnly ? "read-only" : "write") == test.Expected,
                    _ => throw new ArgumentException("Unknown evaluation kind")
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = "invalid_case";
                diagnostic = ex.GetType().Name;
            }
            if (passed) outcome = "passed";
            if (passed) result.Passed++;
            else result.Failures.Add(test.Name + ": " + test.Kind + " (" + outcome + ")");
            evidence.Add(new(test.Name, test.Kind == "role_mode" ? test.Input : test.Role,
                commit, AgentPolicies.Revision, verdict, outcome, diagnostic));
        }
        ct.ThrowIfCancellationRequested();
        if (runtime is not null && directory is not null)
            Planning.RequirePlanningCheckout(new ProjectInfo { Path = directory }, commit!);
        var output = Directory.CreateDirectory(Path.Combine(store.Home, "evaluation-evidence"));
        File.WriteAllText(Path.Combine(output.FullName, result.Id.ToString("N") + ".json"), Json.Stringify(evidence));
        store.Change(s => s.Evaluations.Add(result));
        return result;
    }

    private static bool DefaultApprovalRequiresHuman()
    {
        var context = new PolicyContext("evaluation", Guid.NewGuid(), Guid.NewGuid(),
            FactoryPolicy.AdoptPlan, PolicyRisk.High, HasCapability: true, Manual: false);
        var now = DateTimeOffset.UtcNow;
        return FactoryPolicy.Decide(context, FactoryPolicy.Baseline, null, now).Outcome != PolicyOutcome.Allow &&
            FactoryPolicy.Decide(context with { Manual = true }, FactoryPolicy.Baseline, null, now).Outcome == PolicyOutcome.Allow;
    }

    private static string IndependentApprovalOutcome(string scenario)
    {
        var actor = Guid.NewGuid().ToString("D");
        var context = new PolicyContext(actor, Guid.NewGuid(), Guid.NewGuid(), FactoryPolicy.AdoptPlan,
            PolicyRisk.High, HasCapability: true, Manual: true);
        var policy = new PolicyVersion(Guid.NewGuid(), AdoptionRule.IndependentApproval);
        var now = DateTimeOffset.UtcNow;
        var approval = scenario switch
        {
            "missing" => null,
            "self" => new PolicyApproval(actor, context.Project, context.Source, policy.Revision, now.AddMinutes(1), true, false),
            "independent" => new PolicyApproval(Guid.NewGuid().ToString("D"), context.Project, context.Source,
                policy.Revision, now.AddMinutes(1), true, false),
            _ => throw new ArgumentException("Unknown approval scenario")
        };
        return FactoryPolicy.Decide(context, policy, approval, now).Outcome.ToString().ToLowerInvariant();
    }

    private static string ValidatePlan(string input)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<FactoryPlan>(input, Json.Options)
                ?? throw new ArgumentException("Planner output is empty");
            SpecificationContracts.ValidateNew(plan);
            return "valid";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            return "invalid";
        }
    }
    private static bool CheckCycle(string content)
    {
        var adjacency = JsonSerializer.Deserialize<List<List<int>>>(content) ?? [];
        var jobs = adjacency.Select(_ => new FactoryJob()).ToList();
        for (var i = 0; i < adjacency.Count; i++)
            jobs[i].DependsOn = adjacency[i].Select(index => jobs[index].Id).ToList();
        return DependencyGraph.WouldCycle(jobs);
    }
}
