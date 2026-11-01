using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceMethodResolver
{
    internal static IReadOnlyList<string> FindTestMethodNames(IReadOnlyList<string> classFiles, string className) =>
        classFiles
            .SelectMany(path => FocusedEvidenceRequestResolver.ParseCSharpRoot(path)
                .DescendantNodes()
                .OfType<TypeDeclarationSyntax>())
            .Where(declaration => FocusedEvidenceRequestResolver.FocusedEvidenceTypeMatches(declaration, className))
            .SelectMany(declaration => declaration.Members.OfType<MethodDeclarationSyntax>())
            .Where(method => method.AttributeLists
                .SelectMany(list => list.Attributes)
                .Any(attribute => IsTestAttribute(attribute.Name.ToString())))
            .Select(method => method.Identifier.ValueText)
            .ToArray();

    internal static bool IsTestAttribute(string attributeName)
    {
        var simpleName = attributeName.Split('.').Last();
        if (simpleName.EndsWith("Attribute", StringComparison.Ordinal))
            simpleName = simpleName[..^"Attribute".Length];
        return simpleName is "Fact" or "Theory";
    }
}
