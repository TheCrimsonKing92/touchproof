namespace Mcg.AgentOrchestrator.Core;

internal static class WorkerReadProcedure
{
    private static readonly IReadOnlyList<string> Steps = Array.AsReadOnly(new[]
    {
        "- Before each file read, reuse needed lines already printed this session, including supplied excerpts.",
        "- If the location is unknown, search once with `rg -n` or Grep. Read the needed unread range once with 40 lines on each side, within file bounds.",
        "- Read a whole file only if it has fewer than 300 lines or you need most of it, normally once per session. Batch known ranges in one command.",
        "- Reread printed lines only after you edit the file or a build or test reports a line in it."
    });

    internal static IReadOnlyList<string> ForRole(AgentRole role) =>
        role is AgentRole.Developer or AgentRole.Planner ? Steps : [];

    internal static int CharacterCountForRole(AgentRole role) =>
        ForRole(role).Sum(line => line.Length + Environment.NewLine.Length);
}
