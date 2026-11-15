namespace Factory;

internal static class ClaudeAssets
{
    internal static void Install(string applicationDirectory, string userProfile, bool overwrite)
    {
        var source = Path.Combine(applicationDirectory, "providers", "claude");
        var agents = Path.Combine(source, "agents");
        var skills = Path.Combine(source, "skills");
        if (!Directory.Exists(agents) || !Directory.Exists(skills))
            throw new IOException("Claude Code assets are missing. Use the full Factory distribution.");

        var profile = CreateDirectory(Path.Combine(userProfile, ".claude"));
        var agentTarget = CreateDirectory(Path.Combine(profile, "agents"));
        var skillTarget = CreateDirectory(Path.Combine(profile, "skills"));
        foreach (var file in Directory.EnumerateFiles(agents, "factory-*.md"))
            InstallFile(file, Path.Combine(agentTarget, Path.GetFileName(file)), overwrite);
        foreach (var folder in Directory.EnumerateDirectories(skills, "factory-*"))
        {
            var target = CreateDirectory(Path.Combine(skillTarget, Path.GetFileName(folder)));
            InstallFile(Path.Combine(folder, "SKILL.md"), Path.Combine(target, "SKILL.md"), overwrite);
        }
    }

    private static string CreateDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Claude Code asset directory is a link: {path}");
        return directory.FullName;
    }

    private static void InstallFile(string source, string target, bool overwrite)
    {
        if (File.Exists(target) && !overwrite)
        {
            Console.WriteLine("SKIP " + target);
            return;
        }
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, target, overwrite);
        }
        finally { File.Delete(temporary); }
        Console.WriteLine("INSTALLED " + target);
    }
}
