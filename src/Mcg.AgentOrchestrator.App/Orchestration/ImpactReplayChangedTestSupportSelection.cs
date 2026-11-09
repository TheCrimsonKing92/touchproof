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
        var (runnable, _) = RunnableClasses(checkout, routing);
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

    internal static (HashSet<string> Runnable, IReadOnlyList<string> Excluded) RunnableClasses(
        string checkout, ImpactReplayChangedSourceRouting routing)
    {
        // Preserve the gate's source set and cache entry, including manifest-owned support sources.
        var laneFiles = LaneTestSourceRootResolver.SourceFiles(checkout, LaneTestSourceRootResolver.Resolve(checkout));
        var runnable = AcceptanceTestClassSourceScanner.Scan(checkout).Select(c => c.FullName).ToHashSet(StringComparer.Ordinal);
        var lanePaths = laneFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectPaths = routing.TestProjects.SelectMany(p => p.Sources).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supportFiles = laneFiles.Where(path => !projectPaths.Contains(path)).ToArray();
        var excluded = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var project in routing.TestProjects.Where(p => p.Sources.Any(path => !lanePaths.Contains(path))))
        {
            // Resolve each additional project independently; unrelated projects must not supply constants.
            var files = project.Sources.Concat(supportFiles).Where(File.Exists).Select(Path.GetFullPath)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            runnable.UnionWith(ScanTolerant(project.Path, files, excluded).Select(c => c.FullName));
        }
        runnable.ExceptWith(excluded);
        return (runnable, excluded.ToArray());
    }

    private static IReadOnlyList<AcceptanceTestClassDescriptor> ScanTolerant(
        string cacheKey, string[] files, ISet<string> excluded)
    {
        string? temporary = null;
        var removed = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                try
                {
                    return AcceptanceTestClassSourceScanner.Scan(temporary ?? cacheKey, files)
                        .Where(c => !removed.Contains(c.FullName)).ToArray();
                }
                catch (InvalidDataException error) when (UnresolvedCollection(error.Message) is { } name)
                {
                    // Each retry must remove a new class's attributes; other scanner failures propagate.
                    if (!removed.Add(name)) throw;
                    var replaced = false;
                    for (var index = 0; index < files.Length; index++)
                    {
                        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(files[index])).GetRoot();
                        var declarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                            .Where(node => FullName(node) == name && node.AttributeLists.SelectMany(list => list.Attributes)
                                .Any(IsCollection)).ToArray();
                        if (declarations.Length == 0) continue;
                        // Keep declarations and facts so siblings and inherited tests remain discoverable.
                        // Descendants of an excluded base no longer inherit its unresolved collection.
                        var rewritten = root.ReplaceNodes(declarations, (_, node) => node.WithAttributeLists(
                            SyntaxFactory.List(node.AttributeLists.Select(list => list.WithAttributes(
                                SyntaxFactory.SeparatedList(list.Attributes.Where(attribute => !IsCollection(attribute)))))
                                .Where(list => list.Attributes.Count > 0))));
                        temporary ??= Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("impact-replay-collections"),
                            Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temporary);
                        var copy = Path.Combine(temporary, index + ".cs");
                        File.WriteAllText(copy, rewritten.ToFullString());
                        files[index] = copy;
                        replaced = true;
                    }
                    if (!replaced) throw;
                    excluded.Add(name);
                }
            }
        }
        finally
        {
            if (temporary is not null)
            {
                AcceptanceTestClassSourceCache.Current.Forget(temporary);
                if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static string? UnresolvedCollection(string message)
    {
        const string prefix = "Cannot resolve test collection on '";
        const string suffix = "'.";
        return message.StartsWith(prefix, StringComparison.Ordinal) && message.EndsWith(suffix, StringComparison.Ordinal) &&
            message.Length > prefix.Length + suffix.Length ? message[prefix.Length..^suffix.Length] : null;
    }

    private static bool IsCollection(AttributeSyntax attribute) =>
        attribute.Name.ToString().Split('.').Last() is "Collection" or "CollectionAttribute";

    // Match the scanner's syntax identity, including nested and partial declarations.
    private static string FullName(ClassDeclarationSyntax node) => string.Join(".",
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString())
            .Append(string.Join("+", node.Ancestors().OfType<ClassDeclarationSyntax>().Reverse()
                .Select(item => item.Identifier.ValueText).Append(node.Identifier.ValueText))));

    private static string FullName(INamedTypeSymbol type) => type.ContainingType is { } parent
        ? FullName(parent) + "+" + type.Name
        : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.Name;
}
