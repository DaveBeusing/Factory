using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;

namespace Factory;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { return await ExecuteAsync(args); }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled"); return 130; }
        catch (Exception e) { Console.Error.WriteLine("ERROR: " + e.Message); return 1; }
    }

    private static string Home => Environment.GetEnvironmentVariable("FACTORY_HOME") is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".factory");
    private static FactorySettings Settings(string home)
    {
        var settingsFile = Path.Combine(home, "settings.json");
        var settings = FactorySettings.Load(settingsFile);
        if (settings.EnsureCredentials())
        {
            var temporary = settingsFile + ".tmp";
            File.WriteAllText(temporary, Json.Stringify(settings));
            File.Move(temporary, settingsFile, true);
        }
        return settings;
    }
    private static string Option(string[] args, string key, string fallback = "")
    {
        var at = Array.IndexOf(args, "--" + key);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
    }
    private static bool Flag(string[] args, string key) => args.Contains("--" + key);
    private static string Required(string[] args, string key)
    {
        var value = Option(args, key);
        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Missing --" + key) : value;
    }
    private static Guid GuidArg(string text) => Guid.TryParse(text, out var id) ? id : throw new ArgumentException("Invalid ID: " + text);
    private static void Print(object value) => Console.WriteLine(Json.Stringify(value));

    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
Factory | C# / .NET 10
  version | doctor | init <repo> --name NAME [--base master] [--checks-json PATH]
  enqueue --project NAME --title TITLE --description TEXT [--mode local|remote] [--paths file1,file2] --reviewed-existing-work
  run | recover | reconcile <job-guid> --reviewed --reason TEXT | status | pause | resume | verify <job-guid>
  goal add --project NAME --objective TEXT [--criteria TEXT] [--budget TOKENS]
  plan --goal GUID | adopt --goal GUID [--mode local|remote] [--reviewed-existing-work]
  graph --project NAME | impact --project NAME --file RELATIVE_PATH
  knowledge --project NAME [--topic TOPIC --content TEXT]
  analytics | eval --suite FILE | test-audit --project NAME [--enqueue --reviewed-existing-work]
  release --project NAME --version X.Y.Z
  dashboard [--port 8787] | worker --server HTTPS_URL --workspace DIR --id WORKER_ID
  worker-token --id WORKER_ID
  usage | install-skills [--overwrite]
Environment: FACTORY_HOME overrides the local state directory.
""");
            return 0;
        }
        if (args[0] is "version" or "--version")
        {
            var assembly = typeof(Program).Assembly;
            Console.WriteLine(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "unavailable");
            return 0;
        }
        // Remote workers never initialize or read controller settings and credentials.
        if (args[0] == "worker")
        {
            var token = Environment.GetEnvironmentVariable("FACTORY_WORKER_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Set FACTORY_WORKER_TOKEN (not the administrator token)");
            await RemoteWorker.RunAsync(new Uri(Required(args, "server")), token,
                Required(args, "workspace"), Required(args, "id"), CancellationToken.None);
            return 0;
        }
        var store = new StateStore(Home);
        var settings = Settings(Home);
        var engine = new FactoryEngine(store, settings, FactoryPlugins.Load(Path.Combine(Home, "plugins.json")));
        switch (args[0])
        {
            case "usage":
                Print(await UsageMonitor.GetAsync(Home, CancellationToken.None)); return 0;
            case "doctor":
            {
                var tools = new[] { "dotnet", "git", "codex", "gh" };
                foreach (var tool in tools)
                {
                    try
                    {
                        var r = Commands.Run(tool, ["--version"], Environment.CurrentDirectory, 15);
                        var location = tool == "codex" && r.Ok ? $" [{Commands.ResolveAgentExecutable()}]" : "";
                        Console.WriteLine($"{tool}: {(r.Ok ? r.Output.Trim() : "unavailable")}{location}");
                    }
                    catch (Exception ex) { Console.WriteLine($"{tool}: unavailable ({ex.Message})"); }
                }
                Console.WriteLine("State: " + Home);
                Console.WriteLine("API token file: " + Path.Combine(Home, "settings.json"));
                return 0;
            }
            case "init":
            {
                if (args.Length < 2) throw new ArgumentException("Missing repository path");
                var repo = GitOperations.RepositoryRoot(args[1]);
                var name = Required(args, "name");
                var baseBranch = Option(args, "base", "master");
                if (baseBranch != "master")
                    throw new InvalidOperationException("All Factory work packages must be based on master");
                GitOperations.Git(repo, "rev-parse", "--verify", "refs/heads/master");
                var checksFile = Option(args, "checks-json");
                var checks = string.IsNullOrEmpty(checksFile) ? new List<CheckCommand>() : CheckLoader.Read(checksFile);
                var project = new ProjectInfo
                {
                    Name = name, Path = repo, Origin = GitOperations.Origin(repo) ?? "",
                    BaseBranch = baseBranch, Checks = checks
                };
                store.Change(s =>
                {
                    if (s.Projects.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        GitOperations.SamePath(p.Path, repo))) throw new InvalidOperationException("Project already registered");
                    s.Projects.Add(project);
                });
                Print(project); return 0;
            }
            case "enqueue":
            {
                var p = store.Project(Required(args, "project"));
                var mode = Option(args, "mode", "local");
                if (mode is not ("local" or "remote")) throw new ArgumentException("Invalid mode");
                var title = Required(args, "title");
                var description = Required(args, "description");
                WorkPackagePreflight.RequireMaster(p);
                var preflight = WorkPackagePreflight.InspectDirectJob(store.Read(), p, title, description);
                if (preflight is not null && !Flag(args, "reviewed-existing-work"))
                    throw new InvalidOperationException(
                        "Inspect master code and open work first; explicitly pass --reviewed-existing-work");
                var job = store.AddJob(new FactoryJob
                {
                    ProjectId = p.Id, Title = title, Description = description,
                    Mode = mode, Priority = int.Parse(Option(args, "priority", "50")),
                    Risk = Option(args, "risk", "medium"), MaxRounds = settings.MaxReviewRounds,
                    ExpectedPaths = Option(args, "paths").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    DependsOn = Option(args, "depends").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(GuidArg).ToList()
                });
                if (preflight is not null)
                    store.Event(job.Id, "job_preflight", "master " + preflight.BaseCommit +
                        "; source candidates " + preflight.ExistingCode.Count +
                        "; existing work " + preflight.ExistingJobs.Count +
                        "; matching PRs " + preflight.OpenPullRequests.Count +
                        "; PR inventory checked " + preflight.PullRequestsChecked);
                Print(job); return 0;
            }
            case "run": await engine.RunQueueAsync(CancellationToken.None); return 0;
            case "recover": Print(engine.Recover()); return 0;
            case "reconcile":
                if (args.Length < 2 || !args.Contains("--reviewed", StringComparer.Ordinal))
                    throw new ArgumentException("Use reconcile <job-guid> --reviewed --reason TEXT");
                Print(JobRecovery.ReconcileLocal(store, GuidArg(args[1]), true, Required(args, "reason")));
                return 0;
            case "status":
                Print(new { store.Read().Projects, store.Read().Jobs, store.Read().Goals, store.Read().Workers }); return 0;
            case "pause": store.Change(s => s.Paused = true); Console.WriteLine("Queue paused"); return 0;
            case "resume": store.Change(s => s.Paused = false); Console.WriteLine("Queue resumed"); return 0;
            case "verify": await engine.VerifyIntegrationAsync(GuidArg(args[1])); Console.WriteLine("Integration confirmed"); return 0;
            case "goal":
            {
                if (args.Length < 2 || args[1] != "add") throw new ArgumentException("Use goal add");
                var p = store.Project(Required(args, "project"));
                var goal = new FactoryGoal
                {
                    ProjectId = p.Id, Objective = Required(args, "objective"),
                    AcceptanceCriteria = Option(args, "criteria").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    TokenBudget = long.Parse(Option(args, "budget", "100000"))
                };
                store.Change(s => s.Goals.Add(goal)); Print(goal); return 0;
            }
            case "plan":
            {
                var goal = store.Read().Goals.Single(g => g.Id == GuidArg(Required(args, "goal")));
                var project = store.Read().Projects.Single(p => p.Id == goal.ProjectId);
                Print(await Planning.ProposeAsync(store, project, goal, AgentRuntimeFactory.Create(settings), CancellationToken.None));
                return 0;
            }
            case "adopt": Print(Planning.Adopt(store, GuidArg(Required(args, "goal")),
                Option(args, "mode", "local"), Flag(args, "reviewed-existing-work"))); return 0;
            case "graph":
            {
                var p = store.Project(Required(args, "project"));
                var nodes = KnowledgeGraph.Scan(p);
                store.Change(s => { s.Graph.RemoveAll(n => n.ProjectId == p.Id); s.Graph.AddRange(nodes); });
                Console.WriteLine($"Indexed {nodes.Count} source files"); return 0;
            }
            case "impact":
            {
                var p = store.Project(Required(args, "project"));
                Print(KnowledgeGraph.Impact(store.Read().Graph.Where(n => n.ProjectId == p.Id).ToArray(), Required(args, "file")));
                return 0;
            }
            case "knowledge":
            {
                var p = store.Project(Required(args, "project"));
                if (Flag(args, "topic") || args.Contains("--topic"))
                {
                    var item = new KnowledgeItem
                    {
                        ProjectId = p.Id, Topic = Required(args, "topic"), Content = Required(args, "content"),
                        SourceSha = GitOperations.Head(p.Path)
                    };
                    store.Change(s => s.Knowledge.Add(item)); Print(item);
                }
                else Print(store.Read().Knowledge.Where(x => x.ProjectId == p.Id));
                return 0;
            }
            case "analytics":
            {
                var data = store.Read();
                Print(new
                {
                    jobs = data.Jobs.Count, integrated = data.Jobs.Count(j => j.Integrated),
                    blocked = data.Jobs.Count(j => j.State == JobStates.Blocked),
                    tokensIn = data.Jobs.Sum(j => j.TokensIn), tokensOut = data.Jobs.Sum(j => j.TokensOut),
                    durations = data.Jobs.Where(j => j.FinishedAt.HasValue && j.StartedAt.HasValue)
                        .Select(j => new { j.Id, seconds = (j.FinishedAt!.Value - j.StartedAt!.Value).TotalSeconds }),
                    evaluations = data.Evaluations
                }); return 0;
            }
            case "eval":
            {
                var projectKey = Option(args, "project");
                var project = string.IsNullOrEmpty(projectKey) ? null : store.Project(projectKey);
                Print(await Evaluations.RunAsync(store, Required(args, "suite"),
                    project == null ? null : AgentRuntimeFactory.Create(settings), project?.Path));
                return 0;
            }
            case "test-audit":
            {
                var p = store.Project(Required(args, "project"));
                Console.WriteLine(await TestEngineering.AuditAsync(store, p, AgentRuntimeFactory.Create(settings),
                    Flag(args, "enqueue"), CancellationToken.None, Flag(args, "reviewed-existing-work")));
                return 0;
            }
            case "release": Print(Releases.Prepare(store, store.Project(Required(args, "project")), Required(args, "version"))); return 0;
            case "dashboard":
                await DashboardHost.RunAsync(store, settings, Option(args, "listen", "http://127.0.0.1:" + Option(args, "port", "8787")),
                    CancellationToken.None); return 0;
            case "worker-token":
                // Print only when explicitly requested by the local controller operator.
                Console.WriteLine(WorkerAuthentication.CreateToken(settings.WorkerTokenKey, Required(args, "id")));
                return 0;
            case "install-skills":
                Skills.Install(Flag(args, "overwrite")); return 0;
            default: throw new ArgumentException("Unknown command: " + args[0]);
        }
    }
}

public static class CheckLoader
{
    public static List<CheckCommand> Read(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object) root = root.GetProperty("checks");
        var checks = new List<CheckCommand>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array)
            {
                var elements = item.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                if (elements.Length > 0) checks.Add(new CheckCommand { Name = elements[0], Executable = elements[0], Arguments = elements.Skip(1).ToList() });
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var c = JsonSerializer.Deserialize<CheckCommand>(item.GetRawText(), Json.Options);
                if (c != null) checks.Add(c);
            }
        }
        if (checks.Count == 0 || checks.Any(c => string.IsNullOrWhiteSpace(c.Executable)))
            throw new ArgumentException("Checks must contain executable commands");
        return checks;
    }
}

public static class Skills
{
    public static void Install(bool overwrite)
    {
        var root = AppContext.BaseDirectory;
        var source = Path.Combine(root, "skills");
        if (!Directory.Exists(source))
            source = Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", "..", "skills"));
        if (!Directory.Exists(source)) throw new IOException("Skills directory not found; use the full distribution");
        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");
        Directory.CreateDirectory(dest);
        foreach (var folder in Directory.GetDirectories(source))
        {
            var target = Path.Combine(dest, Path.GetFileName(folder));
            if (Directory.Exists(target) && !overwrite) { Console.WriteLine("SKIP " + target); continue; }
            if (Directory.Exists(target)) Directory.Delete(target, true);
            CopyTree(folder, target);
            Console.WriteLine("INSTALLED " + target);
        }
        var agentsSource = Path.Combine(Path.GetDirectoryName(source)!, "agents");
        if (!Directory.Exists(agentsSource)) return;
        var agentsTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "agents");
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
