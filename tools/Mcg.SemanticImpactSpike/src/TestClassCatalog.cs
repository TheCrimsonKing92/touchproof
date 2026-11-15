using Microsoft.CodeAnalysis;

namespace Mcg.SemanticImpactSpike;

public static class TestClassCatalog
{
    public static bool IsTest(IMethodSymbol method) => method.GetAttributes().Any(attribute =>
    {
        for (var type = attribute.AttributeClass; type is not null; type = type.BaseType)
            if (type.ToDisplayString() == "Xunit.FactAttribute") return true;
        return false;
    });

    public static IEnumerable<IMethodSymbol> Methods(INamedTypeSymbol type)
    {
        // xUnit runs public inherited facts on concrete derived classes. Respect hiding/overrides.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
            foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            {
                var signature = method.Name + "(" + string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString())) + ")";
                if (seen.Add(signature) && method.DeclaredAccessibility == Accessibility.Public && IsTest(method))
                    yield return method;
            }
    }
}
