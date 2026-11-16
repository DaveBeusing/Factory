using System.Diagnostics;
using Factory;

internal static class ProtectedQualityChecksTests
{
    internal static void VerifyPolicy()
    {
        var check = new CheckCommand { Executable = "test", ContainerImage = "sha256:" + new string('a', 64),
            Arguments = ["space and; separators"] };
        var arguments = ProtectedCheckSource.BuildArguments("fixture", "/tmp/a,b/source.tar", check);
        Require(arguments.Contains("--network=none") && arguments.Contains("--cap-drop=ALL") &&
            arguments.Contains("--read-only") && arguments.Contains("--pull=never") &&
            arguments.Contains("--user=65532:65532"), "Container confinement arguments are missing.");
        Require(arguments.Count(arg => arg == "--mount") == 1 &&
            arguments.Contains("type=bind,\"source=/tmp/a,b/source.tar\",target=/source.tar,readonly") &&
            arguments[^1] == "space and; separators", "Unexpected host mount or check argument transformation.");
        var docker = new ProcessStartInfo("docker");
        docker.Environment["FACTORY_SYNTHETIC_SECRET"] = "fixture";
        docker.Environment["DOCKER_CONFIG"] = "fixture";
        ProtectedCheckSource.ConfigureDocker(docker);
        Require(!docker.Environment.ContainsKey("FACTORY_SYNTHETIC_SECRET") &&
            !docker.Environment.ContainsKey("DOCKER_CONFIG"), "Host context leaked into Docker CLI.");
        try
        {
            QualityCheckPolicy.Validate(QualityCheckPolicy.ProtectedContainer,
                [new() { Executable = "test" }]);
            throw new InvalidOperationException("Protected policy accepted a native check.");
        }
        catch (ArgumentException) { }
        QualityCheckPolicy.Validate(QualityCheckPolicy.TrustedHost, [new() { Executable = "test" }]);
        var project = new ProjectInfo { Checks = [check] };
        var originalRevision = ReviewPolicy.ProjectRevision(project);
        project.CheckExecutionPolicy = QualityCheckPolicy.ProtectedContainer;
        Require(originalRevision != ReviewPolicy.ProjectRevision(project),
            "The protected policy was omitted from the project revision.");
        Require(WorkerAuthorization.RequiredCapabilities(project, new(), new()) is null,
            "Protected checks require a locally enforced boundary.");
    }

    internal static async Task VerifyArchiveAsync()
    {
        var repo = Directory.CreateTempSubdirectory("factory-protected-tree-test-");
        try
        {
            Git("init", "--initial-branch=master", "--template=");
            File.WriteAllText(Path.Combine(repo.FullName, "plain.txt"), "tracked\n");
            File.WriteAllText(Path.Combine(repo.FullName, "rewrite.txt"), "$Format:%H$\n");
            Commit();
            using (await ProtectedCheckSource.CreateAsync(repo.FullName, Git("rev-parse", "HEAD"), default)) { }

            File.WriteAllText(Path.Combine(repo.FullName, ".gitattributes"), "plain.txt export-ignore\n");
            Commit();
            await Reject("omits committed");

            File.WriteAllText(Path.Combine(repo.FullName, ".gitattributes"), "rewrite.txt export-subst\n");
            Commit();
            await Reject("differs from the committed");

            Git("update-index", "--add", "--cacheinfo", "160000," + Git("rev-parse", "HEAD") + ",submodule");
            Git("-c", "user.name=Factory fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "-m", "Add test gitlink");
            await Reject("submodules");
        }
        finally { DeleteFixture(repo); }

        string Git(params string[] args) => Commands.Require(Commands.Run("git", args, repo.FullName), "Fixture Git");
        void Commit()
        {
            Git("add", ".");
            Git("-c", "user.name=Factory fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "-m", "Protected archive fixture");
        }

        async Task Reject(string message)
        {
            try
            {
                using var source = await ProtectedCheckSource.CreateAsync(repo.FullName, Git("rev-parse", "HEAD"), default);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains(message, StringComparison.Ordinal))
            {
                return;
            }
            throw new InvalidOperationException("Protected source accepted an incomplete or changed archive.");
        }
    }

    internal static async Task VerifyDockerAsync(string imageId)
    {
        VerifyPolicy();
        var working = Directory.CreateTempSubdirectory("factory-protected-check-test-");
        var repo = working.CreateSubdirectory("repo");
        var sentinel = Path.Combine(working.FullName, "controller-only.txt");
        const string envKey = "FACTORY_SYNTHETIC_SECRET";
        var previous = Environment.GetEnvironmentVariable(envKey);
        try
        {
            Environment.SetEnvironmentVariable(envKey, "synthetic-secret");
            File.WriteAllText(sentinel, "synthetic-secret");
            File.WriteAllText(Path.Combine(repo.FullName, "tracked.txt"), "source");
            File.WriteAllText(Path.Combine(repo.FullName, ".gitignore"), "ignored.txt\n");
            Git("init", "--initial-branch=master", "--template=");
            Git("add", ".");
            Git("-c", "user.name=Factory fixture", "-c", "user.email=fixture@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "-m", "Protected-check fixture");
            File.WriteAllText(Path.Combine(repo.FullName, "ignored.txt"), "worktree-only");
            var check = new CheckCommand
            {
                Name = "fixture", ContainerImage = imageId, Executable = "/bin/sh",
                Arguments = ["-ec", "test \"$(cat tracked.txt)\" = source && test ! -e ignored.txt && " +
                    "test ! -e .git && test ! -e \"$1\" && test -z \"${FACTORY_SYNTHETIC_SECRET+x}\" && " +
                    "touch output && test -f output", "fixture", sentinel]
            };
            using var source = await ProtectedCheckSource.CreateAsync(repo.FullName, Git("rev-parse", "HEAD"), default);
            var result = await source.RunAsync(check, default);
            Require(result.Ok, "Protected check failed: " + result.Error);
            Require(!File.Exists(Path.Combine(repo.FullName, "output")), "Check writes reached the controller checkout.");
            check.ContainerImage = "sha256:" + new string('0', 64);
            result = await source.RunAsync(check, default);
            Require(!result.Ok, "Missing local image did not fail closed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, previous);
            DeleteFixture(working);
        }

        string Git(params string[] args) => Commands.Require(Commands.Run("git", args, repo.FullName), "Fixture Git");
    }

    private static void DeleteFixture(DirectoryInfo directory)
    {
        // Git for Windows marks newly written loose objects read-only.
        if (OperatingSystem.IsWindows())
        {
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
                if (file.IsReadOnly) file.IsReadOnly = false;
        }
        directory.Delete(recursive: true);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
