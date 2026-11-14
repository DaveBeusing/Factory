namespace Factory;

internal sealed record DoctorTool(string Name, string? Version, string? Executable, string? Error);

internal sealed record DoctorReport(DoctorTool[] Tools, string StateDirectory, string SettingsFile);

internal static class Doctor
{
    internal static async Task<DoctorReport> InspectAsync(string home, CancellationToken cancellationToken = default)
    {
        var tools = await Task.WhenAll(new[] { "dotnet", "git", "codex", "gh" }
            .Select(tool => InspectToolAsync(tool, cancellationToken)));
        return new(tools, home, Path.Combine(home, "settings.json"));
    }

    private static async Task<DoctorTool> InspectToolAsync(string tool, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Commands.RunAsync(tool, ["--version"], Environment.CurrentDirectory, 15,
                cancellationToken: cancellationToken);
            return result.Ok
                ? new(tool, result.Output.Trim(), tool == "codex" ? Commands.ResolveAgentExecutable() : null, null)
                : new(tool, null, null, null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new(tool, null, null, ex.Message);
        }
    }
}
