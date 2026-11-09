using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ImpactReplayChangedSupportReferenceScan
{
    internal static (IReadOnlyList<string> Classes, string? AbandonReason, IReadOnlyList<string> OtherFiles) Select(
        string checkout, IReadOnlyList<string> changedFiles,
        IReadOnlyList<ImpactReplayChangedSourceRouting.Project> testProjects, Func<ISet<string>> runnableClasses,
        ImpactReplayChangedSupportScanProbe? probe = null)
    {
        if (changedFiles.Count == 0) return ([], null, []);
        var paths = testProjects.SelectMany(p => p.Sources).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var trees = paths.ToDictionary(p => p, p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p), StringComparer.OrdinalIgnoreCase);
        var malformed = trees.Values.FirstOrDefault(tree => tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error));
        if (malformed is not null) return ([], $"Changed test support source could not be parsed: {Path.GetRelativePath(checkout, malformed.FilePath)}", []);
        var compilation = CSharpCompilation.Create("ReplayTestSources", trees.Values,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var runnable = runnableClasses();
        var infrastructurePaths = testProjects.Where(p => p.Infrastructure).SelectMany(p => p.Sources).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<(string Path, HashSet<INamedTypeSymbol> Declared, HashSet<string> Found,
            SortedSet<string> Classes, SortedSet<string> Other)>();
        string? unresolved = null;
        foreach (var relativePath in changedFiles)
        {
            var path = Path.GetFullPath(relativePath, checkout);
            if (!File.Exists(path)) continue; // Historical deleted declarations cannot add classes.
            if (!trees.TryGetValue(path, out var changedTree))
            {
                unresolved = $"Changed test support could not be resolved: {relativePath}";
                break;
            }
            var changedModel = compilation.GetSemanticModel(changedTree);
            var declared = changedTree.GetRoot().DescendantNodes()
                .Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                .Select(n => changedModel.GetDeclaredSymbol(n)).OfType<INamedTypeSymbol>()
                .ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            files.Add((relativePath, declared, new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal)));
        }

        if (files.Count == 0) return ([], unresolved, []);
        var filter = new ImpactReplayChangedSupportNameFilter(files.SelectMany(file => file.Declared), trees.Values);
        foreach (var tree in trees.Values)
        {
            probe?.EnterTree(tree.FilePath);
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol owner) continue;
                var name = ImpactReplayChangedTestSupportSelection.FullName(owner);
                if (!runnable.Contains(name)) continue;
                var matched = new bool[files.Count];
                for (var index = 0; index < files.Count; index++)
                    matched[index] = files[index].Declared.Contains(owner);
                var pending = matched.Count(value => !value);
                foreach (var reference in node.DescendantNodes().OfType<NameSyntax>())
                {
                    if (pending == 0) break;
                    if (!filter.Matches(reference)) continue;
                    probe?.Bound(name, ImpactReplayChangedSupportNameFilter.RightmostIdentifier(reference),
                        ImpactReplayChangedSupportNameFilter.IsAttributeName(reference));
                    var symbol = model.GetSymbolInfo(reference).Symbol;
                    if (symbol is IAliasSymbol alias) symbol = alias.Target;
                    if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
                        symbol = constructor.ContainingType;
                    if (symbol is not INamedTypeSymbol type) continue;
                    for (var index = 0; index < files.Count; index++)
                    {
                        if (matched[index] || !files[index].Declared.Contains(type.OriginalDefinition)) continue;
                        matched[index] = true;
                        pending--;
                    }
                }
                for (var index = 0; index < files.Count; index++)
                {
                    if (!matched[index]) continue;
                    files[index].Found.Add(name);
                    if (infrastructurePaths.Contains(tree.FilePath)) files[index].Classes.Add(name);
                    else
                        files[index].Other.Add(Path.GetRelativePath(checkout, tree.FilePath).Replace('\\', '/'));
                }
            }
        }

        // Replay outcomes in input order: later references cannot leak into an earlier abandon.
        var selected = new SortedSet<string>(StringComparer.Ordinal);
        var other = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Found.Count == 0)
                return ([], $"Changed test support has no resolvable test class: {file.Path}", other.ToArray());
            selected.UnionWith(file.Classes);
            other.UnionWith(file.Other);
        }
        return (unresolved is null ? selected.ToArray() : [], unresolved, other.ToArray());
    }
}
