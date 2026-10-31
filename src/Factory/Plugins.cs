using System.Reflection;
using System.Runtime.Loader;

namespace Factory;

// Plugins run in-process with the Factory's privileges. Load only explicitly trusted assemblies.
public interface IFactoryPlugin
{
    string Name { get; }
    Task BeforeJobAsync(ProjectInfo project, FactoryJob job, CancellationToken ct);
    Task AfterGateAsync(ProjectInfo project, FactoryJob job, QualityResult result, CancellationToken ct);
    Task AfterJobAsync(ProjectInfo project, FactoryJob job, CancellationToken ct);
}

public static class FactoryPlugins
{
    public static IReadOnlyList<IFactoryPlugin> Load(string configFile)
    {
        if (!File.Exists(configFile)) return [];
        var paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(configFile), Json.Options) ?? [];
        var plugins = new List<IFactoryPlugin>();
        foreach (var file in paths)
        {
            if (!Path.IsPathFullyQualified(file)) throw new InvalidOperationException("Plugin paths must be absolute");
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
            foreach (var type in assembly.GetTypes().Where(t => typeof(IFactoryPlugin).IsAssignableFrom(t) &&
                t.IsClass && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null))
                plugins.Add((IFactoryPlugin)Activator.CreateInstance(type)!);
        }
        return plugins;
    }
}
