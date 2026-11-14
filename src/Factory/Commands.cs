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
        Action<string>? lineCallback = null, CancellationToken cancellationToken = default,
        Action<ProcessStartInfo>? configure = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var proc = new Process();
        proc.StartInfo = StartInfo(executable, arguments, workingDirectory);
        configure?.Invoke(proc.StartInfo);
        proc.Start();
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
            IOException? inputError = null;
            try
            {
                if (stdin != null)
                {
                    await proc.StandardInput.WriteAsync(stdin.AsMemory(), timeout.Token);
                    await proc.StandardInput.FlushAsync(timeout.Token);
                }
                proc.StandardInput.Close();
            }
            catch (IOException ex) { inputError = ex; }
            await Task.WhenAll(proc.WaitForExitAsync(timeout.Token), stdout, stderr);
            if (inputError is not null && proc.ExitCode == 0)
                throw new IOException("The child process closed its input before receiving the full request", inputError);
        }
        catch (OperationCanceledException ex)
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"Command exceeded its {Math.Max(1, timeoutSeconds)} second timeout.", ex);
            throw;
        }
        return new CommandResult(proc.ExitCode, output.ToString(), error.ToString());
    }
    // Resolve the native CLI once for all Factory code paths (usage, planning,
    // builds, and reviews). ProcessStartInfo with UseShellExecute=false cannot
    // rely on PowerShell aliases or on PATH changes made after Factory started.
    public static string ResolveAgentExecutable() => CodexExecutable.Resolve();

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
        info.Environment.Remove("FACTORY_REMOTE_EXECUTOR_TOKEN");
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
