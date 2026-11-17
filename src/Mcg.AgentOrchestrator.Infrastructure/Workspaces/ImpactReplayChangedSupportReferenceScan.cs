using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

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
        Match(files.Select((file, index) => (index, (IReadOnlySet<INamedTypeSymbol>)file.Declared)).ToArray(), filter, probe);
        var unmatched = Enumerable.Range(0, files.Count).Where(index => files[index].Found.Count == 0).ToArray();
        if (unmatched.Length > 0)
        {
            var closure = new ImpactReplayChangedSupportReferrerClosure(compilation, trees.Values, runnable);
            var expanded = unmatched.Select(index => (Index: index, Types: closure.Expand(files[index].Declared))).ToArray();
            var expandedFilter = new ImpactReplayChangedSupportNameFilter(expanded.SelectMany(file => file.Types), trees.Values);
            Match(expanded, expandedFilter, null);
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

        void Match(IReadOnlyList<(int Index, IReadOnlySet<INamedTypeSymbol> Types)> candidates,
            ImpactReplayChangedSupportNameFilter names, ImpactReplayChangedSupportScanProbe? scanProbe)
        {
            foreach (var tree in trees.Values)
            {
                scanProbe?.EnterTree(tree.FilePath);
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol owner) continue;
                    var name = ImpactReplayChangedTestSupportSelection.FullName(owner);
                    if (!runnable.Contains(name)) continue;
                    var matched = new bool[candidates.Count];
                    for (var index = 0; index < candidates.Count; index++)
                        matched[index] = candidates[index].Types.Contains(owner);
                    var pending = matched.Count(value => !value);
                    foreach (var reference in node.DescendantNodes().OfType<NameSyntax>())
                    {
                        if (pending == 0) break;
                        if (!names.Matches(reference)) continue;
                        scanProbe?.Bound(name, ImpactReplayChangedSupportNameFilter.RightmostIdentifier(reference),
                            ImpactReplayChangedSupportNameFilter.IsAttributeName(reference));
                        if (ImpactReplayChangedSupportReferrerClosure.ReferencedType(model, reference) is not { } type) continue;
                        for (var index = 0; index < candidates.Count; index++)
                        {
                            if (matched[index] || !candidates[index].Types.Contains(type)) continue;
                            matched[index] = true;
                            pending--;
                        }
                    }
                    for (var index = 0; index < candidates.Count; index++)
                    {
                        if (!matched[index]) continue;
                        var file = files[candidates[index].Index];
                        file.Found.Add(name);
                        if (infrastructurePaths.Contains(tree.FilePath)) file.Classes.Add(name);
                        else
                            file.Other.Add(Path.GetRelativePath(checkout, tree.FilePath).Replace('\\', '/'));
                    }
                }
            }
        }
    }
}
