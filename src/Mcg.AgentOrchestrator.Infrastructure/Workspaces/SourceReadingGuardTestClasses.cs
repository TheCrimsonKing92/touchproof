using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class SourceReadingGuardTestClasses
{
    internal static ImpactReplaySourceGuardReport? Read(ImpactReplayTestSourceIndex sources,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes)
        => Project(SourceReaderAnalysisCache.Current.GetOrAnalyze(sources, Extract, Evaluate), classes);

    internal static SourceReaderFileFacts[] Extract(ImpactReplayTestSourceIndex sources, HashSet<string>? paths)
    {
        // Bind checkout declarations against core runtime types, without loading or building test assemblies.
        // Candidate symbols are included conservatively when external dependencies prevent overload resolution.
        var compilation = CSharpCompilation.Create("ImpactReplaySources", sources.Trees,
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var files = new List<SourceReaderFileFacts>();
        var pathsByTree = sources.Trees.Distinct().ToDictionary(tree => tree, tree => SourceReaderFileFacts.RelativePath(sources, tree));
        foreach (var tree in sources.Trees)
        {
            var description = SourceReaderFileFacts.Describe(sources, tree);
            if (paths is not null && !paths.Contains(description.Path)) continue;
            var model = compilation.GetSemanticModel(tree);
            var types = new List<SourceReaderFileFacts.TypeFact>();
            var typeIds = new List<string>();
            foreach (var declaration in tree.GetRoot().DescendantNodes()
                         .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type) continue;
                typeIds.Add(Id(type));
                if (declaration is not ClassDeclarationSyntax owner) continue;
                types.Add(new(Id(type), ImpactReplayTestSourceIndex.ClassName(owner),
                    type.BaseType is { } parent && parent.DeclaringSyntaxReferences.Length != 0 ? Id(parent.OriginalDefinition) : null,
                    owner.BaseList?.WithoutTrivia().ToFullString() ?? ""));
            }
            var members = new List<SourceReaderFileFacts.MemberFact>();
            var ordinal = 0;
            foreach (var declaration in SourceReaderFileFacts.Declarations(tree.GetRoot()))
            {
                var position = ordinal++;
                var owner = declaration.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                var symbol = model.GetDeclaredSymbol(declaration)?.OriginalDefinition;
                if (owner is null || symbol is null) continue;
                var name = ImpactReplayTestSourceIndex.ClassName(owner);
                var calls = new HashSet<string>(StringComparer.Ordinal);
                var snapshot = false;
                var invocations = new List<string[]>();
                foreach (var expression in declaration.DescendantNodes().OfType<ExpressionSyntax>()
                             .Where(node => node is InvocationExpressionSyntax or MemberAccessExpressionSyntax or ObjectCreationExpressionSyntax))
                {
                    var info = model.GetSymbolInfo(expression);
                    var targets = new List<string>();
                    foreach (var target in info.Symbol is { } resolved ? new[] { resolved } : info.CandidateSymbols.ToArray())
                    {
                        var canonical = (target is IMethodSymbol { ReducedFrom: { } unreduced }
                            ? unreduced : target).OriginalDefinition;
                        snapshot |= IsSnapshotRead(canonical);
                        // Metadata and untracked local-function/accessor symbols cannot seed this graph.
                        foreach (var reference in canonical.DeclaringSyntaxReferences)
                        {
                            if (!pathsByTree.TryGetValue(reference.SyntaxTree, out var path) ||
                                reference.GetSyntax() is not { } node ||
                                !(node is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax ||
                                  node is VariableDeclaratorSyntax { Parent: { Parent: BaseFieldDeclarationSyntax } }) ||
                                !node.Ancestors().OfType<ClassDeclarationSyntax>().Any()) continue;
                            var id = Qualify(Id(canonical), path);
                            calls.Add(id);
                            targets.Add(id);
                        }
                    }
                    if (expression is InvocationExpressionSyntax) invocations.Add(targets.Distinct(StringComparer.Ordinal).ToArray());
                }
                members.Add(new(Qualify(Id(symbol), description.Path), symbol.Name, name, Id(symbol.ContainingType!), position,
                    symbol is IFieldSymbol { IsStatic: false } or IMethodSymbol { MethodKind: MethodKind.Constructor } ||
                    declaration is MethodDeclarationSyntax method && method.AttributeLists.SelectMany(list => list.Attributes)
                        .Any(attribute => attribute.Name.ToString().Split('.').Last() is "Fact" or "FactAttribute" or "Theory" or "TheoryAttribute"),
                    snapshot, WholeTreeSourceReaderDetector.RootSeed(declaration), WholeTreeSourceReaderDetector.ProcessSeed(declaration),
                    symbol is IMethodSymbol callable ? callable.Parameters.Select(parameter => parameter.Name).ToArray() : [],
                    calls.Order(StringComparer.Ordinal).ToArray(), declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .Select(call => call.ToString()).ToArray(), invocations.ToArray()));
            }
            files.Add(description with { Types = types.ToArray(), Members = members.ToArray(),
                DeclaredSymbolIds = typeIds.Concat(members.Select(member => member.Id))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() });
        }
        return files.ToArray();
    }

    // Documentation ids contain no spaces; preserve their prefix and use the declaration's path.
    private static string Qualify(string id, string path) => id + " " + path;

    private static string Id(ISymbol symbol)
    {
        // Keep the distinctions made by SymbolEqualityComparer on OriginalDefinition.
        var prefix = symbol is IMethodSymbol { PartialDefinitionPart: not null } ? "implementation:" : "";
        return symbol.GetDocumentationCommentId() is { } id ? prefix + id : "untracked:" + symbol.ToDisplayString();
    }

    internal static SourceReaderAnalysisCache.Analysis Evaluate(ImpactReplayTestSourceIndex sources, SourceReaderFileFacts[] files)
    {
        var hasSnapshotCache = files.Any(file => file.DeclaresSnapshotCache);
        var members = files.SelectMany(file => file.Members).ToArray();
        var edges = members.GroupBy(member => member.Id, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => group.SelectMany(member => member.Targets).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var owners = members.GroupBy(member => member.Owner, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => group.Select(member => member.Id).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var readers = members.Where(member => hasSnapshotCache && member.SnapshotSeed)
            .Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
        WholeTreeSourceReaderDetector.Propagate(readers, edges);
        readers.UnionWith(WholeTreeSourceReaderDetector.Read(sources, files, edges));
        // A concrete test can inherit its runnable method and initialization without declaring a member itself.
        var types = files.SelectMany(file => file.Types).GroupBy(type => type.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var inheritable = members.Where(member => member.Inheritable).GroupBy(member => member.TypeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(member => member.Id).ToArray(), StringComparer.Ordinal);
        foreach (var declaration in files.SelectMany(file => file.Types))
        {
            if (!owners.TryGetValue(declaration.Owner, out var owned)) owners[declaration.Owner] = owned = new(StringComparer.Ordinal);
            var parent = declaration.BaseId;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (parent is not null && visited.Add(parent) && types.TryGetValue(parent, out var type))
            {
                if (inheritable.TryGetValue(parent, out var inherited)) owned.UnionWith(inherited);
                parent = type.BaseId;
            }
        }
        return new(hasSnapshotCache, owners.Where(pair => pair.Value.Any(readers.Contains))
            .Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static ImpactReplaySourceGuardReport? Project(SourceReaderAnalysisCache.Analysis analysis,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        var owners = analysis.ReaderOwners.ToHashSet(StringComparer.Ordinal);
        var names = classes.Where(item => owners.Contains(item.FullName))
            .Select(item => item.FullName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return !analysis.DeclaresSnapshotCache && names.Length == 0 ? null : new(names);
    }

    private static bool IsSnapshotRead(ISymbol symbol) =>
        symbol.ContainingType?.Name == "RepositorySourceSnapshotCache" && symbol.Name == "Get" ||
        symbol.ContainingType?.Name == "RepositorySourceSnapshot" &&
        symbol.Name is "EnumerateFiles" or "GetText" or "GetLines" or "GetSyntaxTree";
}
