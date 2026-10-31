namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeclaredTestFileSelector
{
    internal static bool Matches(string selector, DeclaredTestReportRows.Row row) =>
        Matches(selector, row, OperatingSystem.IsWindows());

    internal static bool Matches(string selector, DeclaredTestReportRows.Row row, bool ignoreCase)
    {
        if (selector.Contains("::", StringComparison.Ordinal)) return false;
        var selectedPath = DeclaredTestIdentityPaths.NormalizeRelative(selector);
        var identityPath = DeclaredTestIdentityPaths.NormalizeRelative(DeclaredTestIdentityPaths.PathPart(row.Identity));
        return selectedPath is not null && identityPath is not null && string.Equals(selectedPath, identityPath,
            ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
