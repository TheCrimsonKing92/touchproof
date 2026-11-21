using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class ImpactReplayChangedSupportReferrerClosure
{
    internal const int DefaultMaximumVisited = 10_000;

    private readonly UsingDirectiveSyntax[] _aliases;
    private readonly ILookup<string, (INamedTypeSymbol Owner, SemanticModel Model, NameSyntax Reference)> _references;
    private readonly Dictionary<string, ILookup<INamedTypeSymbol, INamedTypeSymbol>> _bound = new(StringComparer.Ordinal);

    internal ImpactReplayChangedSupportReferrerClosure(CSharpCompilation compilation, IEnumerable<SyntaxTree> trees)
    {
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
    }

    internal (IReadOnlySet<INamedTypeSymbol> Types, bool Truncated) Expand(
        IEnumerable<INamedTypeSymbol> seeds, int maximumVisited)
    {
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<INamedTypeSymbol>();
        foreach (var seed in seeds.Select(type => type.OriginalDefinition))
        {
            if (visited.Contains(seed)) continue;
            if (visited.Count >= maximumVisited) return (visited, true);
            visited.Add(seed);
            pending.Enqueue(seed);
        }

        while (pending.TryDequeue(out var type))
        {
            var names = new ImpactReplayChangedSupportNameFilter([type], _aliases);
            foreach (var name in names.Names.Order(StringComparer.Ordinal))
            {
                if (!_bound.TryGetValue(name, out var edges))
                {
                    edges = _references[name].SelectMany(item => ReferencedTypes(item.Model, item.Reference)
                        .Select(target => (Target: target, item.Owner)))
                        .ToLookup(item => item.Target, item => item.Owner, (IEqualityComparer<INamedTypeSymbol>)SymbolEqualityComparer.Default);
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
