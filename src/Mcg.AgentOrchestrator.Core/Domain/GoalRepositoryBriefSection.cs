namespace Mcg.AgentOrchestrator.Core;

public static class GoalRepositoryBriefSection
{
    public static IEnumerable<string> Render(IReadOnlyDictionary<string, string>? worktrees, AgentRole? role = null)
    {
        if (worktrees is null) return [];
        var lines = new[] { "\r\n## Repositories" }.Concat(
            worktrees.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"- {entry.Key}: {entry.Value}"));
        if (role is AgentRole.Tester or AgentRole.Reviewer)
            lines = lines.Append($"Evidence-request declared repository keys: {string.Join(", ", worktrees.Keys.Order(StringComparer.Ordinal))}. " +
                "An evidence_request for a non-default repository must carry the `repository` field with its declared key; " +
                "test_project and test_class use that repository's own accepted forms. Omitted repository or default selects the home repository.");
        return lines;
    }
}
