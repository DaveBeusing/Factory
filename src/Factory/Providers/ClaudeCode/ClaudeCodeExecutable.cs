using System.Text.RegularExpressions;

namespace Factory;

internal static class ClaudeCodeExecutable
{
    // Restricted mode first shipped in 2.1.248. Probe required options as well
    // as the version so an incompatible distribution cannot enter the ledger.
    private static readonly Version MinimumVersion = new(2, 1, 248);
    private static readonly string[] RequiredHelpOptions =
        ["--restricted", "--print", "--output-format", "--permission-mode", "--tools"];
    private static readonly SemaphoreSlim ProbeLock = new(1);
    private static (string Path, long Length, DateTime Updated)? verified;

    internal static async Task<string> RequireSupportedAsync(CancellationToken cancellation)
    {
        await ProbeLock.WaitAsync(cancellation);
        try
        {
            var executable = Resolve();
            var file = new FileInfo(executable);
            var fingerprint = (executable, file.Length, file.LastWriteTimeUtc);
            if (verified == fingerprint) return executable;

            var versionOutput = await Commands.RunAsync(executable, ["--version"], AppContext.BaseDirectory,
                timeoutSeconds: 15, cancellationToken: cancellation);
            var version = Regex.Match(versionOutput.Output.Trim(), @"\A(\d+\.\d+\.\d+)(?:\s+\(Claude Code\))?\z");
            if (!versionOutput.Ok || !version.Success ||
                !Version.TryParse(version.Groups[1].Value, out var installed) || installed < MinimumVersion)
                throw new InvalidOperationException($"Claude Code {MinimumVersion} or newer is required for restricted turns");

            // The CLI does not list every supported flag in help. Pass the full
            // profile to the parser, and verify the advertised core capabilities.
            var help = await Commands.RunAsync(executable, ClaudeReadOnlyAgentRuntime.CapabilityProbeArguments,
                AppContext.BaseDirectory,
                timeoutSeconds: 15, cancellationToken: cancellation);
            var flags = Regex.Matches(help.Output + "\n" + help.Error, @"(?<![\w-])--[a-z][a-zA-Z0-9-]*(?![\w-])")
                .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
            var missing = RequiredHelpOptions.Where(flag => !flags.Contains(flag)).ToArray();
            if (!help.Ok || missing.Length != 0)
                throw new InvalidOperationException("Claude Code CLI cannot confirm required headless options" +
                    (missing.Length == 0 ? "" : ": " + string.Join(", ", missing)));

            file.Refresh();
            if (!file.Exists || file.Length != fingerprint.Length || file.LastWriteTimeUtc != fingerprint.LastWriteTimeUtc)
                throw new InvalidOperationException("Claude Code executable changed during capability discovery");
            verified = fingerprint;
            return executable;
        }
        finally { ProbeLock.Release(); }
    }

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
