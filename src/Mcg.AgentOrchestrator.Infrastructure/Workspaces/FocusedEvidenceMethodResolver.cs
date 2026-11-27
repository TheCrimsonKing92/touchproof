namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceMethodResolver
{
    internal static IReadOnlyList<string> FindTestMethodNames(
        IReadOnlyList<string> classFiles, string className, string? worktreePath = null) =>
        classFiles
            .SelectMany(path => FocusedEvidenceDeclarationFactStore.Current.Read(path, worktreePath).Types)
            .Where(type => FocusedEvidenceParsedRootCache.FocusedEvidenceTypeMatches(type.Name, className))
            .SelectMany(type => type.TestMethodNames)
            .ToArray();

    internal static bool IsTestAttribute(string attributeName)
    {
        var simpleName = attributeName.Split('.').Last();
        if (simpleName.EndsWith("Attribute", StringComparison.Ordinal))
            simpleName = simpleName[..^"Attribute".Length];
        return simpleName is "Fact" or "Theory";
    }
}
