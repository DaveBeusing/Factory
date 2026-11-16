using System.Buffers;
using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Factory;

internal static partial class QualityCheckPolicy
{
    internal const string TrustedHost = "trusted-host";
    internal const string ProtectedContainer = "protected-container";

    internal static void Validate(string policy, IReadOnlyList<CheckCommand> checks)
    {
        if (policy is not (TrustedHost or ProtectedContainer))
            throw new ArgumentException("Unknown quality check execution policy.");
        if (policy == ProtectedContainer && checks.Any(check => !LocalImageId().IsMatch(check.ContainerImage ?? "")))
            throw new ArgumentException("Protected checks require a locally installed Linux image ID (sha256: followed by 64 lowercase hexadecimal characters) for every check.");
    }

    [GeneratedRegex(@"\Asha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalImageId();
}

/// <summary>Runs repository checks without mounting the controller's checkout or granting network access.</summary>
internal sealed class ProtectedCheckSource : IDisposable
{
    private static string DockerHost => OperatingSystem.IsWindows()
        ? "npipe:////./pipe/docker_engine"
        : "unix:///var/run/docker.sock";
    private readonly DirectoryInfo _staging = Directory.CreateTempSubdirectory("factory-quality-");
    private string Archive => Path.Combine(_staging.FullName, "source.tar");

    internal static async Task<ProtectedCheckSource> CreateAsync(string worktree, string commit, CancellationToken ct)
    {
        var source = new ProtectedCheckSource();
        try
        {
            var tree = await ReadTreeAsync(worktree, commit, ct);
            var result = await Commands.RunAsync("git",
                ["--no-replace-objects", "archive", "--format=tar", "--output=" + source.Archive, commit],
                worktree, cancellationToken: ct);
            Commands.Require(result, "Export committed source for protected checks");
            await source.VerifyArchiveAsync(tree, ct);
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private sealed record SourceBlob(string Id, string Mode);

    private static async Task<Dictionary<string, SourceBlob>> ReadTreeAsync(string worktree, string commit,
        CancellationToken ct)
    {
        var result = await Commands.RunAsync("git", ["--no-replace-objects", "ls-tree", "-r", "-z", "--full-tree", commit],
            worktree, cancellationToken: ct);
        if (!result.Ok || result.Output.Length >= 1_900_000 || (result.Output.Length != 0 &&
            !result.Output.EndsWith("\0" + Environment.NewLine, StringComparison.Ordinal)))
            throw new InvalidOperationException("Complete Git tree required for protected checks.");
        var tree = new Dictionary<string, SourceBlob>(StringComparer.Ordinal);
        foreach (var record in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (record == Environment.NewLine) continue;
            var separator = record.IndexOf('\t');
            if (separator < 0)
                throw new InvalidOperationException("Invalid Git tree record for protected checks.");
            var fields = record[..separator].Split(' ');
            var path = record[(separator + 1)..];
            if (fields is ["160000", "commit", _])
                throw new InvalidOperationException("Protected checks cannot materialize a tree containing submodules.");
            if (fields is not [("100644" or "100755" or "120000"), "blob", { Length: 40 or 64 }] ||
                path.Length == 0 || path.Any(char.IsControl))
                throw new InvalidOperationException("Unsupported Git tree entry for protected checks.");
            tree.Add(path, new(fields[2], fields[0]));
        }
        return tree;
    }

    private async Task VerifyArchiveAsync(Dictionary<string, SourceBlob> tree, CancellationToken ct)
    {
        await using var stream = File.OpenRead(Archive);
        await using var tar = new TarReader(stream);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory or TarEntryType.GlobalExtendedAttributes) continue;
            if (!tree.Remove(entry.Name, out var expected) || !await MatchesBlobAsync(entry, expected, ct))
                throw new InvalidOperationException("Git archive differs from the committed tree required for protected checks.");
        }
        if (tree.Count != 0)
            throw new InvalidOperationException("Git archive omits committed entries required for protected checks.");
    }

    private static async Task<bool> MatchesBlobAsync(TarEntry entry, SourceBlob expected, CancellationToken ct)
    {
        using var digest = IncrementalHash.CreateHash(expected.Id.Length == 40
            ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
        if (expected.Mode == "120000")
        {
            if (entry.EntryType != TarEntryType.SymbolicLink) return false;
            var target = Encoding.UTF8.GetBytes(entry.LinkName);
            digest.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"blob {target.Length}\0")));
            digest.AppendData(target);
        }
        else
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.Mode.HasFlag(UnixFileMode.UserExecute) != (expected.Mode == "100755")) return false;
            digest.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"blob {entry.Length}\0")));
            if (entry.DataStream is { } data)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int count;
                    while ((count = await data.ReadAsync(buffer, ct)) != 0) digest.AppendData(buffer, 0, count);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset()) == expected.Id;
    }

    internal async Task<CommandResult> RunAsync(CheckCommand check, CancellationToken ct)
    {
        var name = "factory-check-" + Guid.NewGuid().ToString("N");
        try
        {
            return await DockerAsync(BuildArguments(name, Archive, check), check.TimeoutSeconds, ct);
        }
        catch (OperationCanceledException)
        {
            await RemoveContainerAsync(name);
            throw;
        }
        catch (TimeoutException)
        {
            await RemoveContainerAsync(name);
            throw;
        }
    }

    internal static string[] BuildArguments(string name, string archive, CheckCommand check)
    {
        QualityCheckPolicy.Validate(QualityCheckPolicy.ProtectedContainer, [check]);
        // Docker's --mount value is CSV. Quote the complete source field.
        var mount = "type=bind,\"source=" + archive.Replace("\"", "\"\"") + "\",target=/source.tar,readonly";
        return ["run", "--name", name, "--rm", "--pull=never", "--network=none", "--ipc=none",
            "--read-only", "--cap-drop=ALL", "--security-opt=no-new-privileges", "--user=65532:65532",
            "--pids-limit=256", "--tmpfs", "/work:rw,exec,nosuid,nodev,mode=1777,size=2g",
            "--tmpfs", "/tmp:rw,exec,nosuid,nodev,mode=1777,size=1g", "--mount", mount,
            "--env", "HOME=/tmp", "--workdir", "/work", "--entrypoint", "/bin/sh",
            check.ContainerImage, "-ec", "tar -xf /source.tar && exec \"$@\"", "factory-check",
            check.Executable, ..check.Arguments];
    }

    private Task<CommandResult> DockerAsync(IReadOnlyList<string> args, int timeout, CancellationToken ct) =>
        Commands.RunAsync("docker", ["--host", DockerHost, "--config", _staging.FullName, ..args],
            _staging.FullName, timeout, cancellationToken: ct, configure: ConfigureDocker);

    internal static void ConfigureDocker(ProcessStartInfo start)
    {
        // Docker must not import the operator's CLI context, proxy configuration or environment.
        var allowed = new[] { "PATH", "SystemRoot", "WINDIR" };
        foreach (var key in start.Environment.Keys.ToArray())
            if (!allowed.Contains(key, StringComparer.OrdinalIgnoreCase))
                start.Environment.Remove(key);
    }

    private async Task RemoveContainerAsync(string name)
    {
        try { await DockerAsync(["rm", "--force", name], 15, CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        File.Delete(Archive);
        _staging.Delete();
    }
}
