namespace Factory;

internal static class CodexAssets
{
    internal static void Install(string applicationDirectory, string userProfile, bool overwrite)
    {
        var assets = Path.Combine(applicationDirectory, "providers", "codex");
        var source = Path.Combine(assets, "skills");
        var agentsSource = Path.Combine(assets, "agents");
        if (!Directory.Exists(source) || !Directory.Exists(agentsSource))
            throw new IOException("Codex assets are missing; use the full Factory distribution.");
        var dest = Path.Combine(userProfile, ".agents", "skills");
        Directory.CreateDirectory(dest);
        foreach (var folder in Directory.GetDirectories(source))
        {
            var target = Path.Combine(dest, Path.GetFileName(folder));
            if (Directory.Exists(target) && !overwrite) { Console.WriteLine("SKIP " + target); continue; }
            if (Directory.Exists(target)) Directory.Delete(target, true);
            CopyTree(folder, target);
            Console.WriteLine("INSTALLED " + target);
        }
        var agentsTarget = Path.Combine(userProfile, ".codex", "agents");
        Directory.CreateDirectory(agentsTarget);
        foreach (var file in Directory.GetFiles(agentsSource, "*.toml"))
        {
            var target = Path.Combine(agentsTarget, "factory-" + Path.GetFileName(file));
            if (File.Exists(target) && !overwrite) { Console.WriteLine("SKIP " + target); continue; }
            File.Copy(file, target, true);
            Console.WriteLine("INSTALLED " + target);
        }
    }
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source)) CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
