using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class RepositoryScopeResolution
{
    public static IReadOnlyList<DeclaredFileScope> KeepResolvable(
        string text,
        IReadOnlyList<DeclaredFileScope> scopes,
        string defaultRoot,
        GoalRepositoryDeclaration repositories)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var region = BriefScopeSectionSelector.FindScopeRegion(lines);
        var keys = RepositoryScopeLabels.Map(lines, repositories.Repositories);
        var retained = new HashSet<DeclaredFileScope>();
        foreach (var group in scopes.GroupBy(scope => scope.RepositoryKey, StringComparer.Ordinal))
        {
            var root = group.Key == "default" ? defaultRoot : repositories.CheckoutRoots[group.Key];
            // A "new" declaration in another repository must not admit this repository's path.
            var ownText = string.Join('\n', lines.Select((line, index) =>
                region is { } range && index >= range.Start && index < range.End && keys[index] != group.Key
                    ? string.Empty : line));
            retained.UnionWith(BriefScopeSectionSelector.KeepResolvable(ownText, group.ToArray(), root));
        }

        return scopes.Where(retained.Contains).ToArray();
    }
}
