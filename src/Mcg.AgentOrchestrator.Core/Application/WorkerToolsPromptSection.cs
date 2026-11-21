namespace Mcg.AgentOrchestrator.Core;

public static class WorkerToolsPromptSection
{
    private static readonly string[] Lines =
    [
        "",
        "### Worker tools",
        "Use `wt read` to read several known ranges in one call, for example `wt read --root . src/File.cs:10-25 src/Other.cs:42-57`.",
        "Use `wt find` with `--definition` or `--references` to locate symbols, for example `wt find --root . --definition WorkerProfileDispatcher`.",
        "`wt --help` gives the full contract. If `wt find` refuses the worktree because of submodules or git-visible changes, fall back to ordinary reads.",
        ""
    ];

    public static bool AppliesTo(AgentRole role) => role is AgentRole.Planner or AgentRole.Developer;

    internal static IReadOnlyList<string> ForRole(AgentRole role, bool available) =>
        available && AppliesTo(role) ? Lines : [];
}
