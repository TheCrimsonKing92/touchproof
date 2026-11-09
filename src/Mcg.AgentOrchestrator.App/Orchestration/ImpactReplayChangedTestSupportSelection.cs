using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Semantic references distinguish aliases and equal short names in different namespaces.
internal static class ImpactReplayChangedTestSupportSelection
{
    internal static (IReadOnlyList<string> Classes, string? AbandonReason, IReadOnlyList<string> OtherFiles) Read(
        string checkout, IReadOnlyList<string> changedFiles, ImpactReplayChangedSourceRouting routing)
    {
        if (changedFiles.Count == 0) return ([], null, []);
        var paths = routing.TestProjects.SelectMany(p => p.Sources).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var trees = paths.ToDictionary(p => p, p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p), StringComparer.OrdinalIgnoreCase);
        var malformed = trees.Values.FirstOrDefault(tree => tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error));
        if (malformed is not null) return ([], $"Changed test support source could not be parsed: {Path.GetRelativePath(checkout, malformed.FilePath)}", []);
        var compilation = CSharpCompilation.Create("ReplayTestSources", trees.Values,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var runnable = AcceptanceTestClassSourceScanner.Scan(checkout, paths).Select(c => c.FullName).ToHashSet(StringComparer.Ordinal);
        var infrastructurePaths = routing.TestProjects.Where(p => p.Infrastructure).SelectMany(p => p.Sources).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = new SortedSet<string>(StringComparer.Ordinal);
        var other = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relativePath in changedFiles)
        {
            var path = Path.GetFullPath(relativePath, checkout);
            if (!File.Exists(path)) continue; // Historical deleted declarations cannot add classes.
            if (!trees.TryGetValue(path, out var changedTree)) return ([], $"Changed test support could not be resolved: {relativePath}", other.ToArray());
            var changedModel = compilation.GetSemanticModel(changedTree);
            var declared = changedTree.GetRoot().DescendantNodes()
                .Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                .Select(n => changedModel.GetDeclaredSymbol(n)).OfType<INamedTypeSymbol>()
                .ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tree in trees.Values)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol owner) continue;
                    var name = FullName(owner);
                    if (!runnable.Contains(name)) continue;
                    var references = declared.Contains(owner) || node.DescendantNodes().OfType<NameSyntax>().Any(reference =>
                    {
                        var symbol = model.GetSymbolInfo(reference).Symbol;
                        if (symbol is IAliasSymbol alias) symbol = alias.Target;
                        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
                            symbol = constructor.ContainingType;
                        return symbol is INamedTypeSymbol type && declared.Contains(type.OriginalDefinition);
                    });
                    if (!references) continue;
                    found.Add(name);
                    if (infrastructurePaths.Contains(tree.FilePath)) selected.Add(name);
                    else other.Add(Path.GetRelativePath(checkout, tree.FilePath).Replace('\\', '/'));
                }
            }
            if (found.Count == 0) return ([], $"Changed test support has no resolvable test class: {relativePath}", other.ToArray());
        }
        return (selected.ToArray(), null, other.ToArray());
    }

    private static string FullName(INamedTypeSymbol type) => type.ContainingType is { } parent
        ? FullName(parent) + "+" + type.Name
        : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.Name;
}
