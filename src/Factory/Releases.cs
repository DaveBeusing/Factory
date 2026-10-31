using System.Security.Cryptography;

namespace Factory;

public static class Releases
{
    public static ReleaseInfo Prepare(StateStore store, ProjectInfo project, string version, string? output = null)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$"))
            throw new ArgumentException("Version must be SemVer");
        var state = store.Read();
        var complete = state.Jobs.Where(j => j.ProjectId == project.Id && j.Integrated)
            .OrderBy(j => j.FinishedAt).ToArray();
        var sha = GitOperations.Head(project.Path);
        var dir = output == null ? Path.Combine(store.Home, "releases", project.Id.ToString("N"), version) : Path.GetFullPath(output);
        Directory.CreateDirectory(dir);
        var manifest = Path.Combine(dir, "manifest.json");
        var notes = Path.Combine(dir, "CHANGELOG.md");
        File.WriteAllText(notes, $"# Release {version}\n\nCommit: `{sha}`\n\n" +
            string.Join("\n", complete.Select(j => $"- {j.Title} ({j.Id})")) + "\n");
        var archive = Path.Combine(dir, "source.zip");
        var exported = Commands.Run("git", ["-C", project.Path, "archive", "--format=zip", "--output=" + archive, sha],
            Environment.CurrentDirectory, 180);
        Commands.Require(exported, "git archive");
        var files = new[] { notes, archive }.Select(file => new
        {
            path = Path.GetFileName(file), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))
        }).ToList();
        File.WriteAllText(manifest, Json.Stringify(new
        {
            version, commit = sha, project = project.Name, jobs = complete.Select(j => new { j.Id, j.Title, j.HeadSha }),
            artifacts = files, createdAt = DateTimeOffset.UtcNow,
            deployment = "not_performed", tag = "not_created"
        }));
        var release = new ReleaseInfo { ProjectId = project.Id, Version = version, Commit = sha, ManifestPath = manifest };
        store.Change(s => s.Releases.Add(release));
        return release;
    }
}
