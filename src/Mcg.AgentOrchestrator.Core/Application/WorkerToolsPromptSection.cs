namespace Mcg.AgentOrchestrator.Core;

public static class WorkerToolsPromptSection
{
    private static readonly string[] Lines =
    [
        "",
        "### Worker tools",
        "Batch known ranges with `wt read --root . src/File.cs:10-25 src/Other.cs:42-57`.",
        "Find symbols with `wt find --root . --definition WorkerProfileDispatcher` or `--references`.",
        "See `wt --help` for the full contract. If `wt find` refuses a worktree with submodules or git-visible changes, fall back to ordinary reads.",
        ""
    ];

    public static bool AppliesTo(AgentRole role) => role is AgentRole.Planner or AgentRole.Developer;

    internal static IReadOnlyList<string> ForRole(AgentRole role, bool available) =>
        available && AppliesTo(role) ? Lines : [];
}
