using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Project = Mcg.AgentOrchestrator.Infrastructure.ImpactReplayChangedSourceRouting.Project;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class ImpactReplayChangedSupportReferrerClosure
{
    internal const int DefaultMaximumVisited = 10_000;

    internal readonly record struct ProjectType(INamedTypeSymbol Type, Project Project);

    private readonly UsingDirectiveSyntax[] _aliases;
    private readonly ILookup<string, (INamedTypeSymbol Owner, SemanticModel Model, NameSyntax Reference)> _references;
    private readonly ILookup<ProjectType, Project> _assemblyHookReferrers;
    private readonly Dictionary<string, ILookup<ProjectType, ProjectType>> _bound = new(StringComparer.Ordinal);
    private readonly Dictionary<INamedTypeSymbol, Project[]> _declaringProjects = new(SymbolEqualityComparer.Default);
    private readonly ILookup<string, Project> _owners;

    internal ImpactReplayChangedSupportReferrerClosure(CSharpCompilation compilation, IEnumerable<SyntaxTree> trees,
        ILookup<string, Project> owners)
    {
        _owners = owners;
        var declarations = trees.SelectMany(tree =>
        {
            var model = compilation.GetSemanticModel(tree);
            return tree.GetRoot().DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                .Select(node => (Node: node, Model: model, Symbol: model.GetDeclaredSymbol(node)));
        }).ToArray();
        _aliases = ImpactReplayChangedSupportNameFilter.ReadAliases(trees);
        var filter = new ImpactReplayChangedSupportNameFilter(declarations.Select(item => item.Symbol)
            .OfType<INamedTypeSymbol>(), _aliases);
        _references = declarations.Where(item => item.Symbol is INamedTypeSymbol)
            .SelectMany(item => item.Node.DescendantNodes().OfType<NameSyntax>().Where(filter.Matches)
                .SelectMany(reference => ReferenceNames(reference, filter.Names).Select(name =>
                    (Name: name, Owner: ((INamedTypeSymbol)item.Symbol!).OriginalDefinition, item.Model, Reference: reference))))
            .ToLookup(item => item.Name, item => (item.Owner, item.Model, item.Reference), StringComparer.Ordinal);

        var moduleInitializer = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ModuleInitializerAttribute")!;
        var initializerNames = new ImpactReplayChangedSupportNameFilter([moduleInitializer], _aliases);
        var initializers = declarations.Where(item => item.Symbol is INamedTypeSymbol &&
            item.Node is TypeDeclarationSyntax type && type.Members.OfType<MethodDeclarationSyntax>()
                .SelectMany(method => method.AttributeLists.SelectMany(list => list.Attributes))
                .Any(attribute => initializerNames.Matches(attribute.Name) &&
                    SymbolEqualityComparer.Default.Equals(item.Model.GetTypeInfo(attribute).Type, moduleInitializer)))
            .SelectMany(item => owners[item.Model.SyntaxTree.FilePath].Select(project =>
                (Target: new ProjectType(((INamedTypeSymbol)item.Symbol!).OriginalDefinition, project), HookProject: project)));
        // File-level registrations can reference support from another file or project without a containing type.
        var registrations = trees.SelectMany(tree => ((CompilationUnitSyntax)tree.GetRoot()).AttributeLists
            .Where(IsAssemblyOrModuleAttributeList).SelectMany(list => list.DescendantNodes().OfType<NameSyntax>())
            .Where(filter.Matches).SelectMany(reference => ReferencedTypes(compilation.GetSemanticModel(tree), reference))
            .SelectMany(target => owners[tree.FilePath].SelectMany(project => ReferenceProjects(target, project)
                .Select(targetProject => (Target: new ProjectType(target, targetProject), HookProject: project)))));
        _assemblyHookReferrers = initializers.Concat(registrations).ToLookup(item => item.Target, item => item.HookProject);
    }

    internal static bool IsAssemblyOrModuleAttributeList(AttributeListSyntax list) =>
        list.Target?.Identifier.ValueText is "assembly" or "module";

    internal IEnumerable<Project> AssemblyHookProjects(IEnumerable<ProjectType> types) =>
        types.SelectMany(type => _assemblyHookReferrers[type]);

    internal (IReadOnlySet<ProjectType> Types, bool Truncated) Expand(
        IEnumerable<ProjectType> seeds, int maximumVisited)
    {
        var visited = new HashSet<ProjectType>();
        var pending = new Queue<ProjectType>();
        foreach (var seed in seeds)
        {
            if (visited.Contains(seed)) continue;
            if (visited.Count >= maximumVisited) return (visited, true);
            visited.Add(seed);
            pending.Enqueue(seed);
        }

        while (pending.TryDequeue(out var type))
        {
            var names = new ImpactReplayChangedSupportNameFilter([type.Type], _aliases);
            foreach (var name in names.Names.Order(StringComparer.Ordinal))
            {
                if (!_bound.TryGetValue(name, out var edges))
                {
                    edges = _references[name].SelectMany(item => ReferencedTypes(item.Model, item.Reference)
                        .SelectMany(target => _owners[item.Model.SyntaxTree.FilePath].SelectMany(project =>
                            ReferenceProjects(target, project).Select(targetProject =>
                                (Target: new ProjectType(target, targetProject), Owner: new ProjectType(item.Owner, project))))))
                        .ToLookup(item => item.Target, item => item.Owner);
                    _bound.Add(name, edges);
                }
                foreach (var owner in edges[type])
                {
                    if (visited.Contains(owner)) continue;
                    if (visited.Count >= maximumVisited) return (visited, true);
                    visited.Add(owner);
                    pending.Enqueue(owner);
                }
            }
        }
        return (visited, false);
    }

    internal IEnumerable<Project> ReferenceProjects(INamedTypeSymbol type, Project referrer)
    {
        if (!_declaringProjects.TryGetValue(type, out var projects))
            _declaringProjects[type] = projects = type.DeclaringSyntaxReferences
                .SelectMany(declaration => _owners[declaration.SyntaxTree.FilePath]).Distinct().ToArray();
        // Equal names in different assemblies can share one symbol in the source-only compilation.
        // Prefer the referrer's own declaration, then consider only referenced assemblies.
        if (projects.Contains(referrer)) return [referrer];
        return referrer.References is { } references ? projects.Where(project => references.Contains(project.Path)) : projects;
    }

    private static IEnumerable<string> ReferenceNames(NameSyntax reference, IReadOnlySet<string> names)
    {
        var identifier = ImpactReplayChangedSupportNameFilter.RightmostIdentifier(reference);
        if (names.Contains(identifier)) yield return identifier;
        if (ImpactReplayChangedSupportNameFilter.IsAttributeName(reference) && names.Contains(identifier + "Attribute"))
            yield return identifier + "Attribute";
    }

    // In the source-only compilation overload resolution can fail on external parameter types.
    // All source candidates are safe referrer edges, including inaccessible or ambiguous candidates.
    internal static IEnumerable<INamedTypeSymbol> ReferencedTypes(SemanticModel model, NameSyntax reference)
    {
        var info = model.GetSymbolInfo(reference);
        if (info.Symbol is { } symbol && symbol is not IErrorTypeSymbol)
        {
            if (ContainingType(symbol, reference) is { } type) yield return type;
            yield break;
        }
        foreach (var candidate in info.CandidateSymbols)
            if (ContainingType(candidate, reference) is { } type) yield return type;
        if (info.Symbol is IErrorTypeSymbol error)
            foreach (var candidate in error.CandidateSymbols)
                if (ContainingType(candidate, reference) is { } type) yield return type;
    }

    private static INamedTypeSymbol? ContainingType(ISymbol symbol, NameSyntax reference)
    {
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        if (symbol is IMethodSymbol { ReducedFrom: { } extension }) return extension.ContainingType.OriginalDefinition;
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            symbol = constructor.ContainingType;
        if (symbol.IsStatic && IsUnqualified(reference) &&
            symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IFieldSymbol or IPropertySymbol or IEventSymbol)
            symbol = symbol.ContainingType!;
        return (symbol as INamedTypeSymbol)?.OriginalDefinition;
    }

    private static bool IsUnqualified(NameSyntax reference) => reference is SimpleNameSyntax &&
        (reference.Parent switch
        {
            MemberAccessExpressionSyntax access => access.Name != reference,
            MemberBindingExpressionSyntax => false,
            QualifiedNameSyntax qualified => qualified.Right != reference,
            AliasQualifiedNameSyntax alias => alias.Name != reference,
            _ => true
        });
}
