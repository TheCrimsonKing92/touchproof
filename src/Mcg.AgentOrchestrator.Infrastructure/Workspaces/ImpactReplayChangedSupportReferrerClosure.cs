using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Reverse references through support declarations; runnable classes remain selection endpoints.
internal sealed class ImpactReplayChangedSupportReferrerClosure
{
    private readonly SyntaxTree[] _trees;
    private readonly Dictionary<INamedTypeSymbol, List<(SemanticModel Model, SyntaxNode Node)>> _declarations =
        new(SymbolEqualityComparer.Default);

    internal ImpactReplayChangedSupportReferrerClosure(
        CSharpCompilation compilation, IEnumerable<SyntaxTree> trees, ISet<string> runnable)
    {
        _trees = trees.ToArray();
        foreach (var tree in _trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes()
                         .Where(node => node is TypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol type ||
                    runnable.Contains(ImpactReplayChangedTestSupportSelection.FullName(type))) continue;
                type = type.OriginalDefinition;
                if (!_declarations.TryGetValue(type, out var parts)) _declarations.Add(type, parts = []);
                parts.Add((model, node));
            }
        }
    }

    internal IReadOnlySet<INamedTypeSymbol> Expand(IEnumerable<INamedTypeSymbol> seeds)
    {
        var visited = seeds.Select(type => type.OriginalDefinition)
            .ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var filter = new ImpactReplayChangedSupportNameFilter(visited, _trees);
        bool added;
        do
        {
            added = false;
            foreach (var (type, parts) in _declarations)
            {
                if (visited.Contains(type)) continue;
                var referencesClosure = parts.Any(part => References(part.Node).Any(reference =>
                    filter.Matches(reference) && ReferencedType(part.Model, reference) is { } target && visited.Contains(target)));
                if (!referencesClosure) continue;
                visited.Add(type);
                filter.Widen([type]);
                added = true;
            }
        } while (added);
        return visited;
    }

    private static IEnumerable<NameSyntax> References(SyntaxNode node)
    {
        if (node is DelegateDeclarationSyntax declaration)
            return declaration.ReturnType.DescendantNodesAndSelf().OfType<NameSyntax>()
                .Concat(declaration.ParameterList.Parameters.SelectMany(parameter =>
                    parameter.Type?.DescendantNodesAndSelf().OfType<NameSyntax>() ?? []));
        var type = (TypeDeclarationSyntax)node;
        var bases = type.BaseList?.DescendantNodes().OfType<NameSyntax>() ?? [];
        return bases.Concat(type.Members.SelectMany(member => member.DescendantNodesAndSelf().OfType<NameSyntax>()));
    }

    internal static INamedTypeSymbol? ReferencedType(SemanticModel model, NameSyntax reference)
    {
        var symbol = model.GetSymbolInfo(reference).Symbol;
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
            symbol = constructor.ContainingType;
        return (symbol as INamedTypeSymbol)?.OriginalDefinition;
    }
}
