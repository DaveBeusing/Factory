using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Factory;

public static class KnowledgeGraph
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".cpp", ".h", ".hpp", ".js", ".ts", ".tsx", ".py", ".rs", ".go", ".java" };
    public static IReadOnlyList<GraphNode> Scan(ProjectInfo project, int maxFiles = 5000)
    {
        var root = project.Path;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", "dist", "build", ".vs", ".venv", "vendor" };
        var result = new List<GraphNode>();
        var stack = new Stack<string>(); stack.Push(root);
        while (stack.Count > 0 && result.Count < maxFiles)
        {
            var dir = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (Directory.Exists(entry))
                {
                    if (!skip.Contains(Path.GetFileName(entry)) &&
                        !File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) stack.Push(entry);
                    continue;
                }
                if (!Extensions.Contains(Path.GetExtension(entry))) continue;
                if (new FileInfo(entry).Length > 1_000_000) continue;
                string content;
                try { content = File.ReadAllText(entry); }
                catch { continue; }
                var imports = Regex.Matches(content,
                    @"(?m)^\s*(?:using\s+([\w.]+)\s*;|import\s+([\w.*]+)|from\s+([\w.]+)\s+import|#include\s+[<"" ]([^>"" ]+))")
                    .Select(m => m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value ?? "")
                    .Where(s => s.Length > 0).Distinct().Take(60).ToList();
                var symbols = Regex.Matches(content,
                    @"\b(?:class|interface|struct|enum|namespace|record|def|function)\s+([A-Za-z_][A-Za-z_0-9]*)")
                    .Select(m => m.Groups[1].Value).Distinct().Take(80).ToList();
                result.Add(new GraphNode
                {
                    ProjectId = project.Id,
                    File = Path.GetRelativePath(root, entry).Replace('\\', '/'),
                    Imports = imports,
                    Symbols = symbols,
                    Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                });
                if (result.Count >= maxFiles) break;
            }
        }
        return result;
    }
    public static List<string> Impact(IReadOnlyList<GraphNode> graph, string target)
    {
        var node = graph.FirstOrDefault(n => n.File.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (node == null) return [];
        var namespaces = node.Symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return graph.Where(n => n.File != target && n.Imports.Any(im => namespaces.Any(ns => im.EndsWith(ns, StringComparison.OrdinalIgnoreCase))))
            .Select(n => n.File).Distinct().ToList();
    }
}
