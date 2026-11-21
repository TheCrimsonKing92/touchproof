namespace Mcg.AgentOrchestrator.Core;

internal static class TaskBriefHeader
{
    internal static IEnumerable<string> DecisionContextLines(AgentRole role)
    {
        yield return "Decision context: embedded in this brief and .orchestrator-handoff.md in the working directory when present; do not attempt to reach orchestrator state.";
        if (role != AgentRole.Developer)
        {
            yield return "A Planner must map evidence that was never recorded as disposition=undecidable, naming what would settle it, its required source, and why it is unavailable, while planning all remaining criteria in the same round. Escalate only when operator action is required; classified evidence escalations must state retrievable plus the inaccessible store, or never-recorded.";
        }
    }

    internal static TaskBriefSegment WithTaskDescriptionFallback(
        List<string> lines, string taskDescriptionLine, TaskSpec task, bool usesFileAccessContext)
    {
        if (!usesFileAccessContext || task.RequiredRole != AgentRole.Developer)
            return TaskBriefSegment.Fixed(lines);

        var pointer = $"Task: {PromptContextFormatter.TrimPromptTitle(task.Description)} " +
            "(Read current-task.md in the context directory for the full description.)";
        if (taskDescriptionLine.Length <= pointer.Length)
            return TaskBriefSegment.Fixed(lines);

        var collapsedLines = lines.ToArray();
        collapsedLines[lines.LastIndexOf(taskDescriptionLine)] = pointer;
        // Keep the full task description inline until the earlier evidence sections have collapsed.
        return new TaskBriefSegment(lines, collapsedLines);
    }
}
