namespace Factory;

// Enforced work-package contract: a missing or invalid declaration is never unlimited access.
public sealed class ScopeViolationException(string message) : InvalidOperationException(message);

public static class WorkPackageScope
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ScopeViolationException("Contract amendment required: empty scope path");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') ||
            normalized.Any(char.IsControl) ||
            normalized.Contains('*') || normalized.Contains('?'))
            throw new ScopeViolationException("Contract amendment required: unsafe scope path: " + path);
        normalized = normalized.TrimEnd('/');
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new ScopeViolationException("Contract amendment required: unsafe scope path: " + path);
        return normalized;
    }

    public static void ValidateDeclaration(IReadOnlyList<string> expectedPaths)
    {
        if (expectedPaths is not { Count: > 0 and <= 100 })
            throw new ScopeViolationException(
                "Contract amendment required: work package must declare 1-100 expected paths");
        foreach (var path in expectedPaths) Normalize(path);
    }

    // A path can name an exact file or a directory/subtree. A sibling with the
    // same prefix (src/Foo.cs.bak) is never included by an exact file declaration.
    public static bool Includes(string declared, string changed)
    {
        var prefix = Normalize(declared);
        var target = Normalize(changed);
        return target.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> OutOfScope(IReadOnlyList<string> expectedPaths,
        IEnumerable<string> changedPaths)
    {
        ValidateDeclaration(expectedPaths);
        return changedPaths.Where(path =>
                !expectedPaths.Any(expected => Includes(expected, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void Enforce(IReadOnlyList<string> expectedPaths, IEnumerable<string> changedPaths)
    {
        var unexpected = OutOfScope(expectedPaths, changedPaths);
        if (unexpected.Count > 0)
            throw new ScopeViolationException(
                "Contract amendment required for out-of-scope changes: " +
                string.Join(", ", unexpected));
    }

    // Include committed changes, staged and unstaged tracked changes, deletions,
    // both sides of renames (--no-renames), and newly created untracked files.
    // NUL delimiters preserve Git paths containing spaces or newline characters.
    public static IReadOnlyList<string> ChangedPaths(string worktree, string baseBranch)
    {
        if (string.IsNullOrWhiteSpace(baseBranch))
            throw new ScopeViolationException("Missing base branch for scope validation");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddGitPaths(params string[] args)
        {
            var command = Commands.Run("git", ["-C", worktree, ..args],
                Environment.CurrentDirectory);
            if (!command.Ok)
                throw new ScopeViolationException(
                    "Cannot inspect complete Git changes; execution blocked: " + command.Error);
            foreach (var entry in command.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                paths.Add(entry);
        }
        AddGitPaths("diff", "--name-only", "-z", "--no-renames", baseBranch + "...HEAD");
        AddGitPaths("diff", "--name-only", "-z", "--no-renames", "HEAD");
        AddGitPaths("ls-files", "--others", "--exclude-standard", "-z");
        return paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> ValidateWorktree(ProjectInfo project, FactoryJob job)
    {
        var changed = ChangedPaths(job.Worktree, project.BaseBranch);
        Enforce(job.ExpectedPaths, changed);
        // Project-level protected paths are never implicitly granted by a work-package amendment.
        GitOperations.EnforcePaths(changed, project.ProtectedPaths);
        return changed;
    }
}
