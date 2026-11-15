using System.Diagnostics;

namespace Factory;

internal static class ProjectProvisioning
{
    internal static async Task<ProjectInfo> CreateAsync(StateStore store, ProjectInput input, CancellationToken ct = default)
    {
        if (input.Source == "existing")
            return MissionControlManager.CreateProject(store, input);
        if (input.Source is not ("new" or "clone" or "remote"))
            throw new ArgumentException("Choose an existing repository, a new repository, a clone, or a remote URL.");
        MissionControlManager.ValidateProjectInput(input);

        var managed = input.Source == "remote";
        var remote = input.Source == "new" ? null : ValidateRemote(input.RemoteUrl);
        if ((managed && !string.IsNullOrWhiteSpace(input.Path)) ||
            (!managed && !Path.IsPathFullyQualified(input.Path)))
            throw new ArgumentException(managed ? "Remote URL projects use a Factory-managed checkout." :
                "Enter an absolute destination path on the Factory controller.");

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managed
            ? Path.Combine(store.Home, "repositories", Guid.NewGuid().ToString("N"))
            : input.Path));
        var parent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Choose a destination below a filesystem root.");
        if (Path.Exists(destination))
            throw new InvalidOperationException("Choose a destination that does not already exist.");
        if (store.Read().Projects.Any(project =>
            project.Name.Equals(input.Name.Trim(), StringComparison.OrdinalIgnoreCase) ||
            GitOperations.SamePath(project.Path, destination)))
            throw new InvalidOperationException("Project name or path is already registered.");

        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(parent);
        var staging = Path.GetFullPath(Path.Combine(parent, ".factory-project-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(staging);
        try
        {
            if (remote is null)
            {
                await GitAsync(staging, ct, "init", "--initial-branch=master", "--template=");
                await GitAsync(staging, ct, "-c", "user.name=Factory", "-c", "user.email=factory@localhost",
                    "-c", "core.hooksPath=.git/hooks",
                    "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Initialize Factory project");
            }
            else
            {
                // Restrict Git transports even when the host has URL rewrites configured.
                await GitAsync(parent, ct, "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
                    "-c", "protocol.ssh.allow=always", "clone", "--template=", "--no-recurse-submodules",
                    "--branch", "master", "--", remote, staging);
            }
            ct.ThrowIfCancellationRequested();
            if (!GitOperations.SamePath(Path.GetDirectoryName(staging)!, parent) ||
                (File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unexpected project staging directory.");
            Directory.Move(staging, destination);

            input.Path = destination;
            try { return MissionControlManager.CreateProject(store, input); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            {
                throw new InvalidOperationException(
                    $"The repository was prepared at '{destination}', but registration failed. Register it as an existing repository. {ex.Message}", ex);
            }
        }
        finally
        {
            if (Directory.Exists(staging) && GitOperations.SamePath(Path.GetDirectoryName(staging)!, parent) &&
                (File.GetAttributes(staging) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(staging, recursive: true);
        }
    }

    private static string ValidateRemote(string? value)
    {
        var remote = value?.Trim();
        if (remote is not null &&
            System.Text.RegularExpressions.Regex.IsMatch(remote, @"\A[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^\s?#]+\z"))
            return remote;
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var url) ||
            url.Scheme is not ("https" or "ssh") || url.Host.Length == 0 ||
            url.AbsolutePath is "" or "/" || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            (url.Scheme == "https" && url.UserInfo.Length != 0) ||
            Uri.UnescapeDataString(url.UserInfo).Contains(':'))
            throw new ArgumentException("Enter an HTTPS or SSH Git repository URL without embedded credentials.");
        return url.AbsoluteUri;
    }

    private static async Task GitAsync(string directory, CancellationToken ct, params string[] args)
    {
        var result = await Commands.RunAsync("git", args, directory, timeoutSeconds: 180, cancellationToken: ct,
            configure: ConfigureGit);
        Commands.Require(result, "Git " + (args.Contains("clone") ? "clone (requires a remote master branch)" : "initialization"));
    }

    private static void ConfigureGit(ProcessStartInfo start)
    {
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
    }
}
