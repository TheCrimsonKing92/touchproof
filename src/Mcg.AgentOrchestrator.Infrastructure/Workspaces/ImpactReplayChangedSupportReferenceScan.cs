using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ProjectType = Mcg.AgentOrchestrator.Infrastructure.ImpactReplayChangedSupportReferrerClosure.ProjectType;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class ImpactReplayChangedSupportReferenceScan
{
    internal const int DefaultClosureThreshold = 100;

    internal static class CoreLib
    {
        internal static readonly MetadataReference Reference;

        // Initialize on first use so parsing failures can return before loading metadata.
        static CoreLib() => Reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private sealed record TestClass(string Name, string Project, bool Infrastructure, string Source);

    private sealed record ChangedFile(string Path, string FullPath, IReadOnlySet<INamedTypeSymbol> Declared,
        HashSet<TestClass> Direct, string? AbandonReason, string? UnresolvedReason,
        bool Deleted = false, bool HasAssemblyAttributes = false, bool DeclaresTestMethods = false);

    internal static ImpactReplayChangedSupportSelection Select(
        string checkout, IReadOnlyList<string> changedFiles,
        IReadOnlyList<ImpactReplayChangedSourceRouting.Project> testProjects, Func<ISet<string>> runnableClasses,
        ImpactReplayChangedSupportScanProbe? probe = null, int threshold = DefaultClosureThreshold,
        int maximumVisited = ImpactReplayChangedSupportReferrerClosure.DefaultMaximumVisited,
        Func<string, IReadOnlyList<string>>? deletedSourceProjects = null,
        ImpactReplayTestSourceIndex? sources = null)
    {
        if (changedFiles.Count == 0) return ImpactReplayChangedSupportSelection.Empty;
        string Relative(string path) => Path.GetRelativePath(checkout, Path.GetFullPath(path, checkout)).Replace('\\', '/');
        changedFiles = changedFiles.DistinctBy(Relative, StringComparer.OrdinalIgnoreCase).ToArray();
        var changedPaths = changedFiles.Select(Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allProjects = testProjects.Select(project => Relative(project.Path)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        var owners = testProjects.SelectMany(project => project.Sources.Select(source => (source, project)))
            .ToLookup(item => item.source, item => item.project, StringComparer.OrdinalIgnoreCase);
        var indexedTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        if (sources is not null)
            foreach (var tree in sources.Trees)
                indexedTrees.TryAdd(tree.FilePath, tree);
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in owners.Select(group => group.Key).Order(StringComparer.Ordinal))
        {
            try
            {
                var tree = indexedTrees.TryGetValue(source, out var indexedTree)
                    ? indexedTree
                    : CSharpSourceFileRootReader.Read(source).SyntaxTree;
                if (!tree.FilePath.Equals(source, StringComparison.Ordinal))
                    tree = tree.WithFilePath(source);
                if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                    return IncompleteInventory($"Changed test support source could not be parsed: {Path.GetRelativePath(checkout, source)}");
                trees.Add(source, tree);
            }
            catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // The changed-file loop records deleted seeds and preserves replay's legacy result.
                if (!changedPaths.Contains(Relative(source)))
                    return IncompleteInventory($"Test support source is missing: {Relative(source)}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return IncompleteInventory($"Test support source could not be read: {Relative(source)} ({error.GetType().Name})");
            }
        }

        var compilation = CSharpCompilation.Create("ReplayTestSources", trees.Values,
            [CoreLib.Reference],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        probe?.CompilationReference = compilation.References.Single();
        var files = new List<ChangedFile>();
        foreach (var relativePath in changedFiles)
        {
            var path = Path.GetFullPath(relativePath, checkout);
            if (!trees.TryGetValue(path, out var changedTree))
            {
                var deleted = !File.Exists(path);
                files.Add(new(Relative(path), path, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default), [],
                    deleted ? null : $"Changed test support could not be resolved: {relativePath}",
                    deleted ? "changed support source is missing or deleted" : "changed support source could not be mapped", deleted));
                continue;
            }
            var changedModel = compilation.GetSemanticModel(changedTree);
            var root = (CompilationUnitSyntax)changedTree.GetRoot();
            var hasAssemblyAttributes = root.AttributeLists.Any(ImpactReplayChangedSupportReferrerClosure.IsAssemblyOrModuleAttributeList);
            var declared = root.DescendantNodes()
                .Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                .Select(n => changedModel.GetDeclaredSymbol(n)).OfType<INamedTypeSymbol>()
                .Select(type => type.OriginalDefinition)
                .ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var undeclared = declared.Count == 0 && !hasAssemblyAttributes;
            var declaresTestMethods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .SelectMany(method => method.AttributeLists)
                .SelectMany(list => list.Attributes)
                .Any(attribute => ImpactReplayChangedSupportNameFilter.RightmostIdentifier(attribute.Name)
                    is "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute");
            files.Add(new(Relative(path), path, declared, [],
                undeclared ? $"Changed test support has no resolvable test class: {relativePath}" : null,
                undeclared ? "no resolvable support declarations" : null,
                HasAssemblyAttributes: hasAssemblyAttributes, DeclaresTestMethods: declaresTestMethods));
        }

        var classesByType = new Dictionary<ProjectType, HashSet<TestClass>>();
        var candidates = files.Where(file => file.Declared.Count > 0).ToArray();
        ImpactReplayChangedSupportReferrerClosure? closure = null;
        if (candidates.Length > 0)
        {
            ISet<string> runnable;
            try { runnable = runnableClasses(); }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return IncompleteInventory($"Test support class inventory could not be read: {error.Message}");
            }
            closure = new(compilation, trees.Values, owners);
            MatchDirect(candidates, runnable, closure);
        }

        var summaries = new List<ImpactReplayChangedSupportFileSummary>();
        var selected = new HashSet<TestClass>();
        var projectMatches = new HashSet<TestClass>();
        string? abandon = null;
        foreach (var file in files)
        {
            var reached = new HashSet<TestClass>();
            var hookProjects = new HashSet<ImpactReplayChangedSourceRouting.Project>();
            if (file.HasAssemblyAttributes) hookProjects.UnionWith(owners[file.FullPath]);
            var truncated = false;
            if (file.Declared.Count > 0)
            {
                var seeds = file.Declared.SelectMany(type => owners[file.FullPath].Select(project => new ProjectType(type, project)));
                var expansion = closure!.Expand(seeds, maximumVisited);
                truncated = expansion.Truncated;
                hookProjects.UnionWith(closure.AssemblyHookProjects(expansion.Types));
                foreach (var type in expansion.Types)
                    if (classesByType.TryGetValue(type, out var classes)) reached.UnionWith(classes);
            }
            var union = reached.Concat(file.Direct).ToHashSet();
            var reachesAssemblyHook = hookProjects.Count > 0;
            // Unreferenced support selects nothing extra. Test methods with no runnable class still require a fallback.
            var unreferenced = file.Declared.Count > 0 && union.Count == 0 && !truncated &&
                file.UnresolvedReason is null && !reachesAssemblyHook;
            var missingClasses = unreferenced && file.DeclaresTestMethods;
            var unknownProjectReach = file.UnresolvedReason is not null || missingClasses;
            var closureCount = CountClasses(reached);
            var exceedsThreshold = threshold > 0 && closureCount > threshold;
            var projectPaths = hookProjects.Select(project => Relative(project.Path));
            if (!reachesAssemblyHook || threshold <= 0 || exceedsThreshold)
                projectPaths = projectPaths.Concat(union.Select(item => item.Project))
                    .Concat(owners[file.FullPath].Select(project => Relative(project.Path)));
            var deletedProjects = file.Deleted ? deletedSourceProjects?.Invoke(file.FullPath) : null;
            var projects = deletedProjects is { Count: > 0 }
                ? deletedProjects.Select(Relative).Order(StringComparer.Ordinal).ToArray()
                : unknownProjectReach || truncated ? allProjects
                : projectPaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            var reasons = new List<string>();
            if (file.UnresolvedReason is { } reason) reasons.Add(reason);
            if (missingClasses) reasons.Add("no resolvable test class");
            if (reachesAssemblyHook) reasons.Add("support reaches an assembly-wide hook");
            if (truncated) reasons.Add($"support closure truncated at {maximumVisited} declarations");
            if (threshold <= 0) reasons.Add("invalid support closure threshold");
            if (exceedsThreshold)
                reasons.Add($"support closure class count {closureCount} exceeds threshold {threshold}");
            var unresolved = unknownProjectReach || reachesAssemblyHook;
            summaries.Add(new(file.Path, CountClasses(file.Direct), closureCount, !truncated && !unresolved, truncated,
                unresolved, projects, reasons.Count > 0, reasons));

            projectMatches.UnionWith(union);
            // Replay retains earlier OtherFiles and its first abandon reason in input order.
            abandon ??= file.AbandonReason ?? (missingClasses
                ? $"Changed test support has no resolvable test class: {file.Path}" : null);
            if (abandon is null) selected.UnionWith(union);
        }

        return new(abandon is null ? SortedDistinct(selected.Where(item => item.Infrastructure).Select(item => item.Name)) : [],
            abandon, SortedDistinct(selected.Where(item => !item.Infrastructure).Select(item => item.Source)),
            summaries.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray(),
            projectMatches.GroupBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => SortedDistinct(group.Select(item => item.Name)), StringComparer.OrdinalIgnoreCase));

        ImpactReplayChangedSupportSelection IncompleteInventory(string reason) =>
            ImpactReplayChangedSupportSelection.Incomplete(changedPaths, allProjects, reason, threshold);

        void MatchDirect(ChangedFile[] changed, ISet<string> runnable, ImpactReplayChangedSupportReferrerClosure graph)
        {
            var names = new ImpactReplayChangedSupportNameFilter(changed.SelectMany(file => file.Declared), trees.Values);
            foreach (var tree in trees.Values)
            {
                probe?.EnterTree(tree.FilePath);
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol owner) continue;
                    var name = ImpactReplayChangedTestSupportSelection.FullName(owner);
                    if (!runnable.Contains(name)) continue;
                    owner = owner.OriginalDefinition;
                    foreach (var project in owners[tree.FilePath])
                    {
                        var identity = new TestClass(name, Relative(project.Path), project.Infrastructure, Relative(tree.FilePath));
                        var key = new ProjectType(owner, project);
                        if (!classesByType.TryGetValue(key, out var classes)) classesByType.Add(key, classes = []);
                        classes.Add(identity);
                        var matched = changed.Select(file => file.Declared.Contains(owner) && owners[file.FullPath].Contains(project)).ToArray();
                        var pending = matched.Count(value => !value);
                        foreach (var reference in node.DescendantNodes().OfType<NameSyntax>())
                        {
                            if (pending == 0) break;
                            if (!names.Matches(reference)) continue;
                            probe?.Bound(name, ImpactReplayChangedSupportNameFilter.RightmostIdentifier(reference),
                                ImpactReplayChangedSupportNameFilter.IsAttributeName(reference));
                            foreach (var type in ImpactReplayChangedSupportReferrerClosure.ReferencedTypes(model, reference))
                            {
                                for (var index = 0; index < changed.Length; index++)
                                {
                                    if (matched[index] || !changed[index].Declared.Contains(type) ||
                                        !graph.ReferenceProjects(type, project).Any(owners[changed[index].FullPath].Contains)) continue;
                                    matched[index] = true;
                                    pending--;
                                }
                            }
                        }
                        for (var index = 0; index < changed.Length; index++)
                            if (matched[index]) changed[index].Direct.Add(identity);
                    }
                }
            }
        }
    }

    private static int CountClasses(IEnumerable<TestClass> classes) =>
        classes.Select(item => (item.Project, item.Name)).Distinct().Count();

    private static IReadOnlyList<string> SortedDistinct(IEnumerable<string> names) =>
        names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
