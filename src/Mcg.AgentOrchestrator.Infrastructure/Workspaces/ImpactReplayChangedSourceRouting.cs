using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class ImpactReplayChangedSourceRouting
{
    internal sealed record Project(string Path, bool Infrastructure, IReadOnlyList<string> Sources)
    {
        // A synthetic source inventory may omit project-reference information.
        internal IReadOnlySet<string>? References { get; init; }
    }
    internal IReadOnlyList<Project> TestProjects { get; }
    private readonly string _checkout;

    internal ImpactReplayChangedSourceRouting(string checkout)
    {
        _checkout = checkout;
        var projects = new List<Project>();
        var references = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Enumerate(checkout, "*.csproj"))
        {
            var document = XDocument.Load(path);
            var directory = System.IO.Path.GetDirectoryName(path)!;
            references[path] = document.Descendants().Where(e => e.Name.LocalName == "ProjectReference" &&
                    !string.Equals((e.Attribute("ReferenceOutputAssembly")?.Value ??
                        e.Elements().FirstOrDefault(child => child.Name.LocalName == "ReferenceOutputAssembly")?.Value)?.Trim(),
                        "false", StringComparison.OrdinalIgnoreCase))
                .SelectMany(e => (e.Attribute("Include")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                .Select(include => System.IO.Path.GetFullPath(include.Replace('\\', '/'), directory)).ToArray();
            var infrastructure = System.IO.Path.GetFileName(path).Equals(
                "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.OrdinalIgnoreCase);
            if (!infrastructure && !System.IO.Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) &&
                !document.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))) continue;
            var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!document.Descendants().Any(e => e.Name.LocalName == "EnableDefaultCompileItems" && e.Value.Trim() == "false"))
                sources.UnionWith(FileSystemTestClassDeclarationReader.EnumerateProjectSourceFiles(directory));
            foreach (var item in document.Descendants().Where(e => e.Name.LocalName == "Compile"))
            {
                foreach (var include in (item.Attribute("Include")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var full = System.IO.Path.GetFullPath(include.Replace('\\', '/'), directory);
                    if (!full.Contains('*')) sources.Add(full);
                    else
                    {
                        var wildcard = full.IndexOf('*');
                        var parent = System.IO.Path.GetDirectoryName(full[..wildcard])!;
                        if (Directory.Exists(parent)) sources.UnionWith(Enumerate(parent, "*.cs").Where(source => Matches(full, source)));
                    }
                }
                foreach (var remove in (item.Attribute("Remove")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                    sources.RemoveWhere(source => Matches(remove, System.IO.Path.GetRelativePath(directory, source)));
            }
            projects.Add(new(path, infrastructure, sources.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        TestProjects = projects.Select(project => project with { References = ReachableProjects(project.Path) }).ToArray();

        HashSet<string> ReachableProjects(string path)
        {
            var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(references[path]);
            while (pending.TryDequeue(out var target))
                if (reached.Add(target) && references.TryGetValue(target, out var next))
                    foreach (var reference in next) pending.Enqueue(reference);
            return reached;
        }
    }

    internal bool IsTestSource(string relativePath)
    {
        var path = System.IO.Path.GetFullPath(relativePath, _checkout);
        return TestProjects.Any(p => p.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)) ||
            (!File.Exists(path) && DeletedSourceProjects(path).Count > 0);
    }

    internal IReadOnlyList<string> DeletedSourceProjects(string path)
    {
        // Deleted default compile items still belong to their nearest project.
        var nearest = NearestProject(path);
        var owners = TestProjects.Where(p => string.Equals(p.Path, nearest, StringComparison.OrdinalIgnoreCase) ||
            p.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)).Select(project => project.Path).ToArray();
        return TestProjects.Where(project => owners.Contains(project.Path, StringComparer.OrdinalIgnoreCase) ||
            owners.Any(owner => project.References?.Contains(owner) == true)).Select(project => project.Path).ToArray();
    }

    private string? NearestProject(string path)
    {
        for (var directory = System.IO.Path.GetDirectoryName(path); directory is not null &&
            directory.StartsWith(_checkout, StringComparison.OrdinalIgnoreCase); directory = System.IO.Path.GetDirectoryName(directory))
        {
            var project = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault() : null;
            if (project is not null) return project;
        }
        return null;
    }

    private static IEnumerable<string> Enumerate(string directory, string pattern)
    {
        foreach (var path in Directory.EnumerateFiles(directory, pattern)) yield return path;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (System.IO.Path.GetFileName(child) is "bin" or "obj" or ".git" or ".scratch" or "artifacts" or ".orchestrator" or ".orchestrator-context" or ".orchestrator-prototype" or ".mcg-sandbox" or ".orchestrator-worktrees" or "TestResults" or "playwright-report") continue;
            foreach (var path in Enumerate(child, pattern)) yield return path;
        }
    }

    private static bool Matches(string pattern, string path)
    {
        var expression = System.Text.RegularExpressions.Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.IsMatch(path.Replace('\\', '/'), "^" + expression + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
