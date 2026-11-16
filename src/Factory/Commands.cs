using System.Diagnostics;
using System.Text;

namespace Factory;

public sealed record CommandResult(int Code, string Output, string Error)
{
    public bool OutputTruncated { get; init; }
    public bool ErrorTruncated { get; init; }
    internal bool CallbackLineTruncated { get; init; }
    public bool Ok => Code == 0 && !OutputTruncated;
}

public static class Commands
{
    private const int MaxCaptureChars = 2_000_000;

    public static Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, int timeoutSeconds = 120, string? stdin = null,
        Action<string>? lineCallback = null, CancellationToken cancellationToken = default,
        Action<ProcessStartInfo>? configure = null)
        => RunCoreAsync(executable, arguments, workingDirectory, timeoutSeconds, stdin, lineCallback,
            cancellationToken, configure, MaxCaptureChars, false);

    internal static Task<CommandResult> RunForDiagnosticsAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken,
        Action<ProcessStartInfo>? configure = null)
        => RunCoreAsync(executable, arguments, workingDirectory, timeoutSeconds, null, null,
            cancellationToken, configure, 4_000, true);

    private static async Task<CommandResult> RunCoreAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, int timeoutSeconds, string? stdin, Action<string>? lineCallback,
        CancellationToken cancellationToken, Action<ProcessStartInfo>? configure, int captureLimit, bool keepTail)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var proc = new Process();
        proc.StartInfo = StartInfo(executable, arguments, workingDirectory);
        configure?.Invoke(proc.StartInfo);
        proc.Start();
        var stdout = DrainAsync(proc.StandardOutput, captureLimit, keepTail, lineCallback, timeout.Token);
        var stderr = DrainAsync(proc.StandardError, captureLimit, keepTail, null, timeout.Token);
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
        return new CommandResult(proc.ExitCode, stdout.Result.Text, stderr.Result.Text)
        {
            OutputTruncated = stdout.Result.Truncated,
            ErrorTruncated = stderr.Result.Truncated,
            CallbackLineTruncated = stdout.Result.OversizeLine
        };
    }

    private static async Task<(string Text, bool Truncated, bool OversizeLine)> DrainAsync(StreamReader reader,
        int limit, bool keepTail, Action<string>? callback, CancellationToken ct)
    {
        var buffer = new char[8192];
        var capture = new StringBuilder();
        var pending = callback is null ? null : new StringBuilder();
        var truncated = false;
        var oversizeLine = false;
        var skipRecord = false;
        var previousCr = false;
        var pendingNewline = false;

        void Capture(ReadOnlySpan<char> text)
        {
            var overflow = capture.Length + text.Length - limit;
            if (overflow > 0) truncated = true;
            if (overflow > 0 && keepTail)
            {
                var discard = Math.Min(overflow, capture.Length);
                capture.Remove(0, discard);
                text = text[(overflow - discard)..];
            }
            capture.Append(text[..Math.Min(text.Length, limit - capture.Length)]);
        }

        void AddLinePart(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty) return;
            Capture(text);
            pendingNewline = true;
            if (pending is null || skipRecord) return;
            if (text.Length <= MaxCaptureChars - pending.Length) pending.Append(text);
            else { skipRecord = true; oversizeLine = true; pending.Clear(); }
        }

        void EndLine()
        {
            Capture(Environment.NewLine);
            if (pending is not null && !skipRecord) callback!(pending.ToString());
            pending?.Clear();
            skipRecord = false;
            pendingNewline = false;
        }

        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            var start = 0;
            for (var i = 0; i < length; i++)
            {
                var ch = buffer[i];
                if (ch is '\r' or '\n')
                {
                    AddLinePart(buffer.AsSpan(start, i - start));
                    if (ch == '\r' || !previousCr) EndLine();
                    start = i + 1;
                }
                previousCr = ch == '\r';
            }
            AddLinePart(buffer.AsSpan(start, length - start));
        }
        if (pendingNewline) EndLine();
        return (capture.ToString(), truncated, oversizeLine);
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
        RemoveInheritedCredentials(info);
        return info;
    }

    internal static void RemoveInheritedCredentials(ProcessStartInfo info)
    {
        info.Environment.Remove("FACTORY_TOKEN");
        info.Environment.Remove("FACTORY_WORKER_TOKEN");
        info.Environment.Remove("FACTORY_REMOTE_EXECUTOR_TOKEN");
        info.Environment.Remove("FACTORY_GITHUB_WEBHOOK_SECRET");
        info.Environment.Remove("FACTORY_WORKFLOW_EVENT_PRIVATE_KEY");
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
        if (result.Code == 0 && result.OutputTruncated)
            throw new InvalidOperationException($"{command} produced incomplete output (capture limit reached)");
        if (!result.Ok) throw new InvalidOperationException($"{command} failed: {result.Error.Trim()}");
        return result.Output.Trim();
    }
}
