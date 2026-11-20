namespace Mcg.AgentOrchestrator.Core;

internal static class WorkerReadProcedure
{
    private static readonly IReadOnlyList<string> Steps = Array.AsReadOnly(new[]
    {
        "- Before reading, check whether this session already printed the lines you need, including in supplied excerpts. If so, use those lines.",
        "- When you need unread lines and their location is unknown, locate them with one `rg -n` or Grep search. Read the needed range once with 40 lines before and 40 lines after, stopping at file boundaries.",
        "- Read a whole file only when it has fewer than 300 lines or you need most of it. Normally read it in full only once per session. Batch known ranges into one command.",
        "- Reread previously printed lines only after you changed the file or a build or test reported a line in it. Read additional unseen lines in a bounded range when needed."
    });

    internal static IReadOnlyList<string> ForRole(AgentRole role) =>
        role is AgentRole.Developer or AgentRole.Planner ? Steps : [];
}
