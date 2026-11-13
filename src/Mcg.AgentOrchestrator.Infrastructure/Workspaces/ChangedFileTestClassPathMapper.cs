using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ChangedFileTestClassPathMapper
{
    internal static ReverseDependencyTestSelection Read(IEnumerable<string> changedFiles,
        ImpactReplayTestSourceIndex sources, IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        IReadOnlyList<string> testProjectDirectories)
    {
        var eligible = changedFiles.Select(Normalize).Where(IsEligible)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (eligible.Length == 0) return ReverseDependencyTestSelection.Resolved([]);
        var known = classes.Select(item => item.FullName).ToHashSet(StringComparer.Ordinal);
        var literals = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in sources.Trees)
        foreach (var literal in tree.GetRoot().DescendantNodes().OfType<LiteralExpressionSyntax>()
                     .Where(node => node.IsKind(SyntaxKind.StringLiteralExpression) || node.IsKind(SyntaxKind.Utf8StringLiteralExpression)))
        {
            var owner = literal.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (owner is null) continue;
            var name = ImpactReplayTestSourceIndex.ClassName(owner);
            if (!known.Contains(name)) continue;
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
            if (matches.Count == 0 && !testProjectDirectories.Any(directory =>
                    Normalize(directory).TrimEnd('/') is { Length: > 0 } folder &&
                    path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)))
                return ReverseDependencyTestSelection.Abandoned(ReverseDependencyDegradationKind.ChangedFileUnmapped,
                    $"Changed file has no naming test class: {path}");
            selected.UnionWith(matches);
        }
        return ReverseDependencyTestSelection.Resolved(selected);
    }

    private static bool IsEligible(string path) => !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        (Path.GetExtension(path).ToLowerInvariant() is ".ps1" or ".cmd" or ".sh" ||
         path.Split('/').SkipLast(1).Contains("Fixtures", StringComparer.OrdinalIgnoreCase));

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
