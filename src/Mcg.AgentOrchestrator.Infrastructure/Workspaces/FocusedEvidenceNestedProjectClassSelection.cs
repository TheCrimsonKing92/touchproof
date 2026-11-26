using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceNestedProjectClassSelection
{
    internal static bool ProjectContainsClass(string worktreePath, string project, string className) =>
        FocusedEvidenceRequestResolver.FindFocusedEvidenceClassFiles(worktreePath, project, className).Any(path =>
            RepositoryProjectOwnership.FindNestedProjectFile(worktreePath, Path.GetDirectoryName(project)!, path) is null);

    internal static List<(string Target, string Project, FocusedEvidenceFilter? Filter)> Apply(
        string worktreePath,
        IReadOnlyList<(string Target, string Project, FocusedEvidenceFilter? Filter)> validated,
        IReadOnlyList<FocusedEvidenceDroppedSelection> priorDrops,
        out IReadOnlyList<FocusedEvidenceDroppedSelection> dropped)
    {
        var result = new List<(string Target, string Project, FocusedEvidenceFilter? Filter)>();
        var omissions = new List<FocusedEvidenceDroppedSelection>(priorDrops);
        var discoveries = new Dictionary<string, SourceDeclarations[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in validated)
        {
            if (item.Filter is not { } filter)
            {
                result.Add(item);
                continue;
            }

            if (!discoveries.TryGetValue(item.Project, out var sources))
                discoveries[item.Project] = sources = ReadDeclarations(worktreePath, item.Project);

            var retained = new List<FocusedEvidenceFilterToken>();
            foreach (var token in filter.Tokens)
            {
                var owner = IsPositive(token)
                    ? FindNestedOwner(worktreePath, item.Project, sources, token.ContainingClass)
                    : null;
                if (owner is null)
                    retained.Add(token);
                else
                    omissions.Add(new(item.Target, item.Project, token.Value, [$"owned-by-nested-project:{owner}"]));
            }

            if (retained.Count == filter.Tokens.Count)
                result.Add(item);
            // Repeated positives of one kind are ORed. Every original positive group must
            // survive because different kinds are ANDed together.
            else if (filter.Tokens.Where(IsPositive).Select(token => token.Kind).Distinct()
                .All(kind => retained.Any(token => token.Kind == kind)))
                result.Add((item.Target, item.Project, filter with { Tokens = retained,
                    CanonicalText = FocusedEvidenceRequestResolver.CanonicalLaunchFilter(retained) }));
        }

        dropped = omissions;
        return result;
    }

    private static bool IsPositive(FocusedEvidenceFilterToken token) =>
        token.Kind is FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method;

    private static SourceDeclarations[] ReadDeclarations(string worktreePath, string project)
    {
        try
        {
            return FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(worktreePath, project)
                .Select(path => new SourceDeclarations(path, CSharpSourceFileRootReader.ReadFile(path).Names))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? FindNestedOwner(
        string worktreePath, string project, SourceDeclarations[] sources, string containingClass)
    {
        var projectDirectory = Path.GetDirectoryName(project)!;
        string? firstOwner = null;
        foreach (var source in sources)
        {
            // Launch filters can match a substring, so a parent-owned match keeps the selector.
            if (!source.Names.Any(name => name.QualifiedName.Contains(containingClass, StringComparison.OrdinalIgnoreCase)))
                continue;

            var owner = RepositoryProjectOwnership.FindNestedProjectFile(worktreePath, projectDirectory, source.Path);
            if (owner is null)
                return null;
            firstOwner ??= owner;
        }

        return firstOwner;
    }

    private sealed record SourceDeclarations(
        string Path, System.Collections.Immutable.ImmutableArray<FocusedEvidenceParsedRootCache.DeclaredTypeName> Names);
}
