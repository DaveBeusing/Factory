namespace Factory;

internal static class ClaudeCodeExecutable
{
    internal static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("FACTORY_CLAUDE_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
                OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("FACTORY_CLAUDE_EXECUTABLE must name an existing absolute path to the native CLI");
            return path;
        }

        var name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(entry => entry.Trim().Trim('"'))
            .Where(Path.IsPathFullyQualified)
            .Select(entry => Path.Combine(entry, name));
        return Commands.FirstExistingExecutable(candidates.Prepend(Path.Combine(home, ".local", "bin", name)))
            ?? throw new FileNotFoundException("Claude Code CLI not found. Install the native CLI or set FACTORY_CLAUDE_EXECUTABLE.");
    }
}
