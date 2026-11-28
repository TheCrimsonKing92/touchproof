using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceChangedTestClassSeeds(
    HashSet<string> WholeProjects,
    HashSet<string> MappedSources,
    Dictionary<string, HashSet<string>> DeclaredClassesByProject)
{
    internal static AcceptanceChangedTestClassSeeds Classify(string worktreePath,
        ImpactReplayChangedSourceRouting routing, IReadOnlyList<string> changedPaths,
        IReadOnlyList<AcceptanceWholeProjectCheck> targets,
        Func<AcceptanceWholeProjectCheck, AcceptanceWholeProjectCheckInventory> inventoryFor)
    {
        var wholeProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredClasses = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var check in targets)
        {
            var projectPath = Path.GetFullPath(check.Project, worktreePath);
            var project = routing.TestProjects.FirstOrDefault(item =>
                item.Path.Equals(projectPath, StringComparison.OrdinalIgnoreCase));
            var directory = Path.GetDirectoryName(projectPath)! + Path.DirectorySeparatorChar;
            HashSet<string>? inventory = null;
            foreach (var path in changedPaths)
            {
                var inDirectory = path.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
                var inSources = project?.Sources.Contains(path, StringComparer.OrdinalIgnoreCase) == true;
                if (!inDirectory && !inSources) continue;

                var classes = inDirectory && inSources && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? ReadDeclaredClasses(path) : [];
                if (classes.Length > 0)
                {
                    inventory ??= inventoryFor(check).Classes.Select(item => item.FullName).ToHashSet(StringComparer.Ordinal);
                    if (inventory.IsSupersetOf(classes))
                    {
                        if (!declaredClasses.TryGetValue(check.Project, out var names))
                            declaredClasses.Add(check.Project, names = new(StringComparer.Ordinal));
                        names.UnionWith(classes);
                        mappedSources.Add(Path.GetRelativePath(worktreePath, path));
                        continue;
                    }
                }

                wholeProjects.Add(check.Project);
            }
        }

        return new(wholeProjects, mappedSources, declaredClasses);
    }

    private static string[] ReadDeclaredClasses(string path)
    {
        try
        {
            var root = CSharpSourceFileRootReader.Read(path);
            return root.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                ? [] : root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(ImpactReplayTestSourceIndex.ClassName).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
