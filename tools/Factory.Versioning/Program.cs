using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Factory.Versioning;

/// <summary>
/// Local-only release versioning. Does not call GitHub, use tags or edit master.
/// VersionPrefix, FactoryReleaseChannel and FactoryReleaseSequence are owned by Directory.Build.props.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help")
            {
                Console.WriteLine("Factory.Versioning: build-version | bump major|minor|patch | channel preview|rc|stable | self-test [--repo PATH]");
                return 0;
            }

            if (args[0] == "self-test")
            {
                SelfTest.Run();
                Console.WriteLine("Versioning self-tests passed.");
                return 0;
            }

            var pathIndex = Array.IndexOf(args, "--repo");
            var root = pathIndex >= 0 && pathIndex + 1 < args.Length
                ? Path.GetFullPath(args[pathIndex + 1])
                : Directory.GetCurrentDirectory();
            var doc = VersionDocument.Load(Path.Combine(root, "Directory.Build.props"));

            switch (args[0])
            {
                case "build-version":
                    RequireArgs(args, 1);
                    Console.WriteLine(doc.State.Effective(GitFirstParentCount(root)));
                    return 0;
                case "bump":
                    RequireArgs(args, 2);
                    doc.Set(doc.State.Bump(args[1]));
                    Console.WriteLine(doc.State.Effective(null));
                    return 0;
                case "channel":
                    RequireArgs(args, 2);
                    doc.Set(doc.State.SwitchChannel(args[1]));
                    Console.WriteLine(doc.State.Effective(null));
                    return 0;
                default:
                    throw new ArgumentException("Unknown versioning command: " + args[0]);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("ERROR: " + e.Message);
            return 1;
        }
    }

    private static void RequireArgs(string[] args, int required)
    {
        if (args.Length < required || (required == 2 && args[1].StartsWith("--", StringComparison.Ordinal)))
            throw new ArgumentException("Missing command argument. Run with --help.");
    }

    /// <summary>
    /// First-parent count grows after merges into master without modifying a shared file.
    /// A shallow clone cannot produce a reliable count, so refuse it.
    /// Extracted source archives without Git use the explicit fallback sequence.
    /// </summary>
    private static int? GitFirstParentCount(string root)
    {
        var toplevel = Git(root, "rev-parse", "--show-toplevel");
        if (toplevel == null) return null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(toplevel)),
                           Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), comparison))
            return null;
        if (Git(root, "rev-parse", "--is-shallow-repository") == "true")
            throw new InvalidOperationException("Preview versions require a complete Git history (fetch-depth: 0).");
        var count = Git(root, "rev-list", "--first-parent", "--count", "HEAD");
        if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            throw new InvalidOperationException("Unable to calculate the first-parent commit count.");
        return number;
    }

    private static string? Git(string root, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("Git version calculation timed out.");
            }
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // Git is optional for offline source archives.
        }
    }
}

internal sealed record VersionState(int Major, int Minor, int Patch, string Channel, int Sequence)
{
    public string Prefix => $"{Major}.{Minor}.{Patch}";

    public string Effective(int? previewCount)
    {
        return Channel switch
        {
            "stable" => Prefix,
            "rc" => $"{Prefix}-rc.{Sequence}",
            "preview" => $"{Prefix}-preview.{Math.Max(Sequence, previewCount ?? Sequence)}",
            _ => throw new InvalidOperationException("Invalid release channel.")
        };
    }

    public VersionState Bump(string level) => level switch
    {
        "major" => this with { Major = checked(Major + 1), Minor = 0, Patch = 0, Channel = "preview", Sequence = 0 },
        "minor" => this with { Minor = checked(Minor + 1), Patch = 0, Channel = "preview", Sequence = 0 },
        "patch" => this with { Patch = checked(Patch + 1), Channel = "preview", Sequence = 0 },
        _ => throw new ArgumentException("Bump must be major, minor or patch.")
    };

    public VersionState SwitchChannel(string channel) => channel switch
    {
        "preview" => this with { Channel = "preview", Sequence = 0 },
        "rc" => this with { Channel = "rc", Sequence = Channel == "rc" ? checked(Sequence + 1) : 1 },
        "stable" => this with { Channel = "stable", Sequence = 0 },
        _ => throw new ArgumentException("Channel must be preview, rc or stable.")
    };
}

internal sealed class VersionDocument
{
    private static readonly Regex PrefixPattern = new(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$",
        RegexOptions.CultureInvariant);
    private readonly string _path;
    private readonly XDocument _document;
    private readonly XElement _group;

    public VersionState State { get; private set; }

    private VersionDocument(string path, XDocument document, XElement group, VersionState state)
    {
        _path = path;
        _document = document;
        _group = group;
        State = state;
    }

    public static VersionDocument Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Directory.Build.props not found.", path);
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var group = doc.Root?.Elements("PropertyGroup").SingleOrDefault(g => g.Element("VersionPrefix") != null)
            ?? throw new InvalidOperationException("Exactly one VersionPrefix is required.");
        string Read(string name) =>
            group.Element(name)?.Value.Trim() ?? throw new InvalidOperationException($"Missing {name}.");
        var match = PrefixPattern.Match(Read("VersionPrefix"));
        if (!match.Success) throw new InvalidOperationException("VersionPrefix must be a three-part numeric SemVer.");
        var channel = Read("FactoryReleaseChannel");
        if (channel is not ("preview" or "rc" or "stable"))
            throw new InvalidOperationException("Invalid FactoryReleaseChannel.");
        if (!int.TryParse(Read("FactoryReleaseSequence"), NumberStyles.None, CultureInfo.InvariantCulture,
                          out var sequence) || sequence < 0)
            throw new InvalidOperationException("FactoryReleaseSequence must be a nonnegative integer.");
        if (channel == "rc" && sequence < 1)
            throw new InvalidOperationException("Release candidates require sequence >= 1.");
        if (!int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch))
            throw new InvalidOperationException("Version components exceed Int32 range.");
        return new VersionDocument(path, doc, group, new VersionState(major, minor, patch, channel, sequence));
    }

    public void Set(VersionState state)
    {
        _group.Element("VersionPrefix")!.Value = state.Prefix;
        _group.Element("FactoryReleaseChannel")!.Value = state.Channel;
        _group.Element("FactoryReleaseSequence")!.Value = state.Sequence.ToString(CultureInfo.InvariantCulture);
        _document.Save(_path);
        State = state;
    }
}

internal static class SelfTest
{
    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Self-test failed: " + label);
    }

    public static void Run()
    {
        var s = new VersionState(0, 4, 12, "preview", 0);
        Assert(s.Effective(23) == "0.4.12-preview.23", "Git-based preview revision");
        Assert(s.Effective(null) == "0.4.12-preview.0", "offline fallback");
        Assert(s.Bump("patch").Prefix == "0.4.13", "patch");
        Assert(s.Bump("minor").Prefix == "0.5.0", "minor");
        Assert(s.Bump("major").Prefix == "1.0.0", "major");
        var rc = s.SwitchChannel("rc");
        Assert(rc.Effective(null) == "0.4.12-rc.1", "first release candidate");
        Assert(rc.SwitchChannel("rc").Effective(null) == "0.4.12-rc.2", "subsequent candidate");
        Assert(rc.SwitchChannel("stable").Effective(null) == "0.4.12", "stable");
        var dir = Path.Combine(Path.GetTempPath(), "factory-versioning-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "Directory.Build.props");
            File.WriteAllText(path, "<Project><PropertyGroup><VersionPrefix>0.1.0</VersionPrefix>" +
                "<FactoryReleaseChannel>preview</FactoryReleaseChannel>" +
                "<FactoryReleaseSequence>0</FactoryReleaseSequence></PropertyGroup></Project>");
            var doc = VersionDocument.Load(path);
            doc.Set(doc.State.Bump("minor").SwitchChannel("rc"));
            Assert(VersionDocument.Load(path).State.Effective(null) == "0.2.0-rc.1", "XML roundtrip");
            File.WriteAllText(path, File.ReadAllText(path).Replace("<VersionPrefix>0.2.0</VersionPrefix>",
                                                                   "<VersionPrefix>01.2.0</VersionPrefix>"));
            try { VersionDocument.Load(path); throw new InvalidOperationException("Invalid SemVer accepted."); }
            catch (InvalidOperationException e) when (e.Message.Contains("VersionPrefix", StringComparison.Ordinal)) { }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
