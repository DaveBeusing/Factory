using System.Diagnostics;
using System.Text;

namespace Factory;

public sealed record CommandResult(int Code, string Output, string Error)
{
    public bool Ok => Code == 0;
}

public static class Commands
{
    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, int timeoutSeconds = 120, string? stdin = null,
        Action<string>? lineCallback = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var proc = new Process();
        proc.StartInfo = StartInfo(executable, arguments, workingDirectory);
        proc.Start();
        if (stdin != null) await proc.StandardInput.WriteAsync(stdin.AsMemory(), timeout.Token);
        proc.StandardInput.Close();
        var output = new StringBuilder();
        var error = new StringBuilder();
        async Task DrainAsync(StreamReader reader, StringBuilder into, bool notify)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(timeout.Token)) != null)
            {
                if (into.Length < 2_000_000) into.AppendLine(line);
                if (notify) lineCallback?.Invoke(line);
            }
        }
        var stdout = DrainAsync(proc.StandardOutput, output, true);
        var stderr = DrainAsync(proc.StandardError, error, false);
        try
        {
            await Task.WhenAll(proc.WaitForExitAsync(timeout.Token), stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
            throw;
        }
        return new CommandResult(proc.ExitCode, output.ToString(), error.ToString());
    }
    // Resolve the native CLI once for all Factory code paths (usage, planning,
    // builds, and reviews). ProcessStartInfo with UseShellExecute=false cannot
    // rely on PowerShell aliases or on PATH changes made after Factory started.
    public static string ResolveAgentExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("FACTORY_AGENT_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    "FACTORY_AGENT_EXECUTABLE points to a missing file. Set it to the full path of the native CLI (codex.exe): " + path,
                    path);
            return Path.GetFullPath(path);
        }

        if (!OperatingSystem.IsWindows()) return "codex";

        // The standalone Windows installer normally exposes codex.exe here.
        // Also support older native locations and a WinGet command alias.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(home, ".codex");
        var installDir = Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR");

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(installDir))
            candidates.Add(Path.Combine(Environment.ExpandEnvironmentVariables(installDir), "codex.exe"));
        candidates.Add(Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"));
        candidates.Add(Path.Combine(codexHome, "packages", "standalone", "current", "bin", "codex.exe"));
        candidates.Add(Path.Combine(codexHome, "packages", "standalone", "current", "codex.exe"));
        candidates.Add(Path.Combine(home, ".codex", "bin", "codex.exe"));
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "codex.exe"));

        // Prefer native executables over legacy command wrappers on PATH.
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')))
            .Where(dir => !string.IsNullOrWhiteSpace(dir)).ToList();
        candidates.AddRange(pathEntries.Select(dir => Path.Combine(dir, "codex.exe")));
        candidates.AddRange(pathEntries.Select(dir => Path.Combine(dir, "codex.cmd")));

        var found = FirstExistingExecutable(candidates);
        if (found != null) return found;
        throw new FileNotFoundException(
            "Native CLI (codex.exe) not found. Install it using the official Windows standalone installer, " +
            "restart Factory after installation, or set FACTORY_AGENT_EXECUTABLE to the full path of codex.exe. " +
            "PowerShell aliases are not executable paths.");
    }

    public static string ResolveClaudeExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("FACTORY_CLAUDE_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (!File.Exists(path))
                throw new FileNotFoundException("FACTORY_CLAUDE_EXECUTABLE points to a missing file", path);
            return Path.GetFullPath(path);
        }
        if (!OperatingSystem.IsWindows()) return "claude";
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .SelectMany(path => new[] { Path.Combine(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')), "claude.exe"),
                Path.Combine(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')), "claude.cmd") });
        return FirstExistingExecutable(candidates)
            ?? throw new FileNotFoundException("Claude Code CLI not found. Install the official CLI, restart Factory, or set FACTORY_CLAUDE_EXECUTABLE.");
    }

    // Pure helper for unit testing lookup precedence without installing software.
    public static string? FirstExistingExecutable(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        return null;
    }

    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var supplied = arguments.ToArray();
        var actual = executable;
        var argv = supplied;
        if (executable.Equals("codex", StringComparison.OrdinalIgnoreCase))
            actual = ResolveAgentExecutable();
        else if (executable.Equals("claude", StringComparison.OrdinalIgnoreCase))
            actual = ResolveClaudeExecutable();

        if (OperatingSystem.IsWindows() &&
            (actual.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
             actual.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            var commandLine = QuoteCmd(actual) + " " + string.Join(" ", supplied.Select(QuoteCmd));
            actual = "cmd.exe";
            argv = ["/d", "/s", "/c", "\"" + commandLine + "\""];
        }

        var info = new ProcessStartInfo(actual)
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in argv) info.ArgumentList.Add(arg);
        // The native agent CLI, Git and configured quality checks are untrusted child
        // processes: they must not inherit controller or worker API bearer credentials.
        info.Environment.Remove("FACTORY_TOKEN");
        info.Environment.Remove("FACTORY_WORKER_TOKEN");
        return info;
    }
    private static string QuoteCmd(string argument)
    {
        if (argument.IndexOfAny(['\r', '\n', '%', '!']) >= 0)
            throw new ArgumentException("Unsafe cmd.exe argument");
        return "\"" + argument.Replace("\"", "\"\"") + "\"";
    }

    public static CommandResult Run(string file, IEnumerable<string> args, string cwd, int timeout = 90)
        => RunAsync(file, args, cwd, timeout).GetAwaiter().GetResult();
    public static string Require(CommandResult result, string command)
    {
        if (!result.Ok) throw new InvalidOperationException($"{command} failed: {result.Error.Trim()}");
        return result.Output.Trim();
    }
}
