namespace Mcg.AgentOrchestrator.Core;

public static class GoalRepositoryBriefSection
{
    public static IEnumerable<string> Render(IReadOnlyDictionary<string, string>? worktrees) =>
        worktrees is null ? [] : new[] { "\r\n## Repositories" }.Concat(
            worktrees.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => $"- {entry.Key}: {entry.Value}"));
}
