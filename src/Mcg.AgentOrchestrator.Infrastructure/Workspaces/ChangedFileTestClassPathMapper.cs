using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ChangedFileTestClassPathMapper
{
    private const string AcceptanceManifestPath = "config/acceptance-manifest.json";

    internal static ReverseDependencyTestSelection Read(IEnumerable<string> changedFiles,
        ImpactReplayTestSourceIndex sources, IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        IReadOnlyList<string> testProjectDirectories)
    {
        var changed = changedFiles.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Literal readers cannot prove coverage of the manifest's lane membership and filters.
        if (changed.Contains(AcceptanceManifestPath, StringComparer.OrdinalIgnoreCase))
            return ReverseDependencyTestSelection.Abandoned(ReverseDependencyDegradationKind.ChangedFileUnmapped,
                $"Changed acceptance manifest requires the full gate: {AcceptanceManifestPath}");
        var eligible = changed.Where(path => IsEligible(path, testProjectDirectories)).ToArray();
        if (eligible.Length == 0) return ReverseDependencyTestSelection.Resolved([]);
        var known = classes.Select(item => item.FullName).ToHashSet(StringComparer.Ordinal);
        var literals = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in sources.Trees)
        foreach (var literal in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>()
                     .Where(node => node.IsKind(SyntaxKind.StringLiteralExpression) || node.IsKind(SyntaxKind.Utf8StringLiteralExpression)))
        {
            var name = literal.Ancestors().OfType<ClassDeclarationSyntax>()
                .Select(ImpactReplayTestSourceIndex.ClassName).FirstOrDefault(known.Contains);
            if (name is null) continue;
            var value = Normalize(literal.Token.ValueText);
            if (!literals.TryGetValue(value, out var matches)) literals[value] = matches = new(StringComparer.Ordinal);
            matches.Add(name);
        }
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in eligible)
        {
            var matches = new HashSet<string>(StringComparer.Ordinal);
            if (literals.TryGetValue(path, out var byPath)) matches.UnionWith(byPath);
            if (literals.TryGetValue(path.Split('/').Last(), out var byName)) matches.UnionWith(byName);
            if (matches.Count == 0 && !IsInsideTestProject(path, testProjectDirectories))
            {
                var projectSelection = ReadOwningProjectClasses(path, sources, known, testProjectDirectories);
                if (projectSelection.Outcome != ReverseDependencySelectionOutcome.Resolved) return projectSelection;
                matches.UnionWith(projectSelection.TestClassNames);
            }
            selected.UnionWith(matches);
        }
        return ReverseDependencyTestSelection.Resolved(selected);
    }

    private static ReverseDependencyTestSelection ReadOwningProjectClasses(string path,
        ImpactReplayTestSourceIndex sources, HashSet<string> known, IReadOnlyList<string> testProjectDirectories)
    {
        if (sources.RepositoryRoot is not { } root || sources.Projects is not { } projects)
            return Unmapped();
        var declared = projects.Where(project =>
                DeclaredTestProjectInventory.IsDeclaredFocusedEvidenceProject(Path.GetRelativePath(root, project.Path)) &&
                testProjectDirectories.Any(directory => Normalize(directory).TrimEnd('/').Equals(
                    Normalize(Path.GetRelativePath(root, Path.GetDirectoryName(project.Path)!)), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (testProjectDirectories.Any(directory => !declared.Any(project => Normalize(directory).TrimEnd('/').Equals(
                Normalize(Path.GetRelativePath(root, Path.GetDirectoryName(project.Path)!)), StringComparison.OrdinalIgnoreCase))))
            return Unsupported("Declared test project inventory is incomplete.");
        if (!ChangedFileOwningProjectResolver.TryResolve(root, path, declared.Select(project => project.Path).ToArray(),
                out var referencing, out var failure))
            return failure is null ? Unmapped() : Unsupported(failure);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in declared.Where(project => referencing.Contains(project.Path, StringComparer.OrdinalIgnoreCase)))
        {
            var names = sources.Trees.Where(tree => project.Sources.Contains(tree.FilePath, StringComparer.OrdinalIgnoreCase))
                .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                .Select(ImpactReplayTestSourceIndex.ClassName).Where(known.Contains).ToArray();
            if (names.Length == 0) return Unsupported($"Referencing test project has no known test classes: {project.Path}.");
            selected.UnionWith(names);
        }
        return ReverseDependencyTestSelection.Resolved(selected);

        ReverseDependencyTestSelection Unmapped() => ReverseDependencyTestSelection.Abandoned(
            ReverseDependencyDegradationKind.ChangedFileUnmapped, $"Changed file has no naming test class: {path}");
        ReverseDependencyTestSelection Unsupported(string reason) => ReverseDependencyTestSelection.Abandoned(
            ReverseDependencyDegradationKind.ChangedFileUnmapped, $"Cannot determine project test coverage for changed file: {path}. {reason}");
    }

    private static bool IsEligible(string path, IReadOnlyList<string> testProjectDirectories) =>
        !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        (Path.GetExtension(path).ToLowerInvariant() is ".ps1" or ".cmd" or ".sh" ||
         path.Split('/').SkipLast(1).Contains("Fixtures", StringComparer.OrdinalIgnoreCase) ||
         path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !IsInsideTestProject(path, testProjectDirectories));

    private static bool IsInsideTestProject(string path, IReadOnlyList<string> testProjectDirectories) =>
        testProjectDirectories.Any(directory =>
            Normalize(directory).TrimEnd('/') is { Length: > 0 } folder &&
            path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));

    internal static string[] TestProjectDirectories(AcceptanceGateEngineSettings settings) =>
        DeclaredTestProjectInventory.DeclaredProjects(settings, settings.DeclaredDotnetTestChecks)
            .Select(project => Normalize(project)[..Normalize(project).LastIndexOf('/')])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string Normalize(string value)
    {
        var path = value.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path;
    }
}
