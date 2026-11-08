namespace Factory;

internal static class CodexExecutable
{
    internal static string Resolve()
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

        var found = Commands.FirstExistingExecutable(candidates);
        if (found != null) return found;
        throw new FileNotFoundException(
            "Native CLI (codex.exe) not found. Install it using the official Windows standalone installer, " +
            "restart Factory after installation, or set FACTORY_AGENT_EXECUTABLE to the full path of codex.exe. " +
            "PowerShell aliases are not executable paths.");
    }
}
