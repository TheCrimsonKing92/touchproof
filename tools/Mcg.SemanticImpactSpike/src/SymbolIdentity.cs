using Microsoft.CodeAnalysis;

namespace Mcg.SemanticImpactSpike;

public static class SymbolIdentity
{
    public static ISymbol Normalize(ISymbol symbol)
    {
        if (symbol is IMethodSymbol method)
        {
            if (method.ReducedFrom is not null) symbol = method.ReducedFrom;
            if (method.AssociatedSymbol is not null) symbol = method.AssociatedSymbol;
            if (method.PartialDefinitionPart is not null) symbol = method.PartialDefinitionPart;
        }
        return symbol.OriginalDefinition;
    }

    public static string? Key(ISymbol? symbol)
    {
        if (symbol is null) return null;
        symbol = Normalize(symbol);
        var id = symbol.GetDocumentationCommentId();
        return id is null ? null : symbol.ContainingAssembly.Identity.Name + "|" + id;
    }

    public static string TypeName(INamedTypeSymbol type) => type.ToDisplayString();
}
