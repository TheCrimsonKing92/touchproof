namespace Mcg.AgentOrchestrator.Core;

/// <summary>Projects the persisted intake census; worker briefs never rescan a changing checkout.</summary>
public static class RemovalReferenceCensusBriefSection
{
    public const string StoredPrefix = "Removal reference census:";

    internal static IEnumerable<TaskBriefSegment> Segments(Goal goal, AgentRole role)
    {
        if (role is not (AgentRole.Planner or AgentRole.Developer)) yield break;
        var stored = goal.Timeline.LastOrDefault(evt => evt.Kind == ProgressKind.RemovalReferenceCensusRecorded &&
            evt.Message.StartsWith(StoredPrefix + "\n", StringComparison.Ordinal));
        if (stored is not null)
            yield return TaskBriefSegment.Fixed([
                string.Empty, "## Removal reference census", stored.Message[(StoredPrefix.Length + 1)..], string.Empty
            ]);
    }
}
