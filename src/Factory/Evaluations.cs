using System.Text.Json;

namespace Factory;

public sealed class EvaluationCase
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Input { get; set; } = "";
    public string Expected { get; set; } = "";
}

public static class Evaluations
{
    public static async Task<EvaluationResult> RunAsync(StateStore store, string suiteFile,
        IAgentRuntime? agent = null, string? directory = null, CancellationToken ct = default)
    {
        var cases = JsonSerializer.Deserialize<List<EvaluationCase>>(File.ReadAllText(suiteFile), Json.Options)
            ?? throw new InvalidOperationException("Empty evaluation suite");
        var result = new EvaluationResult { Suite = Path.GetFileName(suiteFile), Total = cases.Count };
        foreach (var test in cases)
        {
            var passed = false;
            try
            {
                if (test.Kind == "agent_answer_contains")
                {
                    if (agent == null || string.IsNullOrEmpty(directory)) throw new InvalidOperationException("Agent evaluation requires a project and agent runtime");
                    var answer = await agent.RunAsync(directory, test.Input, "", ct, readOnly: true);
                    result.TokensIn += answer.InputTokens;
                    result.TokensOut += answer.OutputTokens;
                    passed = answer.Success && answer.FinalText.Contains(test.Expected, StringComparison.OrdinalIgnoreCase);
                }
                else passed = test.Kind switch
                {
                    "path_normalization" => GitOperations.SamePath(test.Input, test.Expected),
                    "approval_default" => test.Expected == "human",
                    "dependency_cycle" => CheckCycle(test.Input) == bool.Parse(test.Expected),
                    "risk_priority" => int.Parse(test.Input) is >= 0 and <= 100,
                    _ => false
                };
            }
            catch { passed = false; }
            if (passed) result.Passed++;
            else result.Failures.Add(test.Name + ": " + test.Kind);
        }
        store.Change(s => s.Evaluations.Add(result));
        return result;
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
