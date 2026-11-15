using System.Text.Json;
using Factory;

internal static class IntakeRegressionTests
{
    internal static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("factory-intake-validation-");
        var previousHome = Environment.GetEnvironmentVariable("FACTORY_HOME");
        try
        {
            var repo = root.CreateSubdirectory("repo").FullName;
            GitOperations.Git(repo, "init", "-b", "master");
            GitOperations.Git(repo, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
                "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Fixture");
            var home = Path.Combine(root.FullName, "state");
            Environment.SetEnvironmentVariable("FACTORY_HOME", home);
            var store = new StateStore(home);
            var project = MissionControlManager.CreateProject(store, new ProjectInput
            {
                Name = "Fixture", Path = repo, RequiredExecutionCapabilities = ["runtime:exec"],
                Checks = [new() { Name = "git", Executable = "git", TimeoutSeconds = 10 }]
            });

            foreach (var (option, invalidValue, mutate) in
                new (string, string, Action<JobInput>)[]
                {
                    ("--risk", "unknown", input => input.Risk = "unknown"),
                    ("--priority", "101", input => input.Priority = 101),
                    ("--paths", "../outside", input => input.ExpectedPaths = ["../outside"])
                })
            {
                var input = NewJob(project.Id);
                mutate(input);
                Reject(() => MissionControlManager.CreateJob(store, input, new FactorySettings()));
                var cli = new List<string>
                {
                    "enqueue", "--project", project.Name, "--title", "Fixture job",
                    "--description", "Validate direct work package intake", "--reviewed-existing-work"
                };
                if (option != "--paths") cli.AddRange(["--paths", "src/"]);
                cli.AddRange([option, invalidValue]);
                Require(await Factory.Program.Main(cli.ToArray()) != 0, "CLI persisted a malformed job");
            }
            var snapshot = store.Read();
            Require(snapshot.Jobs.Count == 0 && snapshot.Events.All(e => e.Type != "queued"),
                "Malformed requests left a queued job or event");

            var checkFile = Path.Combine(root.FullName, "checks.json");
            await File.WriteAllTextAsync(checkFile, """{"checks":[{"name":"git","executable":"git","timeoutSeconds":0}]}""");
            Reject(() => CheckLoader.Read(checkFile));
            Reject(() => MissionControlManager.UpdateProject(store, project.Id, new ProjectInput
            {
                Name = project.Name, Path = repo, Checks = [new() { Name = "git", Executable = "git", TimeoutSeconds = 0 }]
            }));
            await File.WriteAllTextAsync(checkFile, """{"checks":[{"name":"git","executable":"git","timeoutSeconds":10}]}""");
            var checks = CheckLoader.Read(checkFile);
            Require(checks.Single().TimeoutSeconds == 10, "A valid CLI check could not be imported");

            var projectEdit = JsonSerializer.Deserialize<ProjectInput>(
                JsonSerializer.Serialize(new { name = "Renamed", path = repo }), Json.Options)!;
            projectEdit.Checks = checks;
            MissionControlManager.UpdateProject(store, project.Id, projectEdit);
            Require(store.Read().Projects.Single().RequiredExecutionCapabilities.SequenceEqual(["runtime:exec"]),
                "An omitted project requirement list changed stored requirements");
            projectEdit.RequiredExecutionCapabilities = ["unsupported:fixture"];
            Reject(() => MissionControlManager.UpdateProject(store, project.Id, projectEdit));
            projectEdit.RequiredExecutionCapabilities = [];
            MissionControlManager.UpdateProject(store, project.Id, projectEdit);
            Require(store.Read().Projects.Single().RequiredExecutionCapabilities.Count == 0,
                "An explicitly empty project requirement list should clear stored requirements");

            var create = NewJob(project.Id);
            create.RequiredExecutionCapabilities = ["runtime:exec"];
            var job = MissionControlManager.CreateJob(store, create, new FactorySettings());
            var jobEdit = NewJob(project.Id);
            jobEdit.Priority = 60;
            MissionControlManager.UpdateJob(store, job.Id, jobEdit);
            Require(store.Job(job.Id).RequiredExecutionCapabilities.SequenceEqual(["runtime:exec"]),
                "An omitted job requirement list changed stored requirements");
            jobEdit.RequiredExecutionCapabilities = ["unsupported:fixture"];
            Reject(() => MissionControlManager.UpdateJob(store, job.Id, jobEdit));
            jobEdit.RequiredExecutionCapabilities = [];
            MissionControlManager.UpdateJob(store, job.Id, jobEdit);
            Require(store.Job(job.Id).RequiredExecutionCapabilities.Count == 0,
                "An explicitly empty job requirement list should clear stored requirements");

            Require(await Factory.Program.Main([
                "enqueue", "--project", projectEdit.Name, "--title", "Fixture job",
                "--description", "Validate direct work package intake", "--paths", "src/",
                "--priority", "60", "--reviewed-existing-work"
            ]) == 0, "CLI rejected a valid intake request");
            var jobs = store.Read().Jobs;
            var cliJob = jobs.Single(candidate => candidate.Id != job.Id);
            var dashboardJob = jobs.Single(candidate => candidate.Id == job.Id);
            Require(cliJob.Title == dashboardJob.Title && cliJob.Description == dashboardJob.Description &&
                cliJob.Risk == dashboardJob.Risk && cliJob.Mode == dashboardJob.Mode &&
                cliJob.Priority == dashboardJob.Priority && cliJob.ExpectedPaths.SequenceEqual(dashboardJob.ExpectedPaths),
                "CLI and dashboard persisted different work-package fields");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FACTORY_HOME", previousHome);
            try { root.Delete(true); } catch { }
        }
    }

    private static JobInput NewJob(Guid projectId) => new()
    {
        ProjectId = projectId, Title = "Fixture job", Description = "Validate direct work package intake",
        ExpectedPaths = ["src/"], ReviewedExistingWork = true
    };

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Malformed intake was accepted");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
