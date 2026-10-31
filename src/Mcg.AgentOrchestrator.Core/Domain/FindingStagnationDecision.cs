namespace Mcg.AgentOrchestrator.Core;

/// <summary>A durable operator choice for one stagnant finding episode, never a worker role.</summary>
public sealed record FindingStagnationDecision(
    GoalId GoalId,
    string StableId,
    TaskId TriggeringTaskId,
    string Description,
    IReadOnlyList<string> SecondaryDescriptions,
    string? DeveloperBlocker,
    IReadOnlyList<string> RoundKeys,
    bool CitedFilesUntouched,
    DateTimeOffset ResetAt)
{
    public int RoundCount => RoundKeys.Count;
    public string QuestionFingerprint => $"{FingerprintPrefix(GoalId, StableId)}{ResetAt.UtcTicks}:{RoundKeys[^1]}";

    public static string FingerprintPrefix(GoalId goalId, string stableId) =>
        $"finding-stagnation:{goalId.Value}:{stableId}:";

    public string Question =>
        $"Finding stagnation: stable_id={StableId}; rounds={RoundCount}; raising_rounds=[{string.Join(",", RoundKeys)}]. " +
        $"Finding: \"{Description}\". " +
        (SecondaryDescriptions.Count == 0 ? string.Empty :
            $"Other report descriptions: {string.Join("; ", SecondaryDescriptions.Select(text => $"\"{text}\""))}. ") +
        (DeveloperBlocker is null ? "Latest Developer result: no Developer blocker reported for this finding. " :
            $"Latest Developer blocker: \"{DeveloperBlocker}\". ") +
        $"Cited files unchanged: {CitedFilesUntouched}. No further paid retry is admitted until operator action. " +
        $"Use answer <request-id> <ruling-or-new-guidance>, goal-amend {GoalId.Value[..8]} --waive <exact-text> --reason <reason>, " +
        $"or revise {GoalId.Value[..8]} --brief-file <path> --reason <reason>. " +
        $"After answering with new guidance, route it using retry --goal {GoalId.Value[..8]} <task-number> --text-file <path> --cause <cause>.";

    public static string? ReadStableId(GoalId goalId, string fingerprint)
    {
        var prefix = $"finding-stagnation:{goalId.Value}:";
        if (!fingerprint.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var roundSeparator = fingerprint.LastIndexOf(':');
        var resetSeparator = roundSeparator > 0 ? fingerprint.LastIndexOf(':', roundSeparator - 1) : -1;
        return resetSeparator > prefix.Length ? fingerprint[prefix.Length..resetSeparator] : null;
    }
}
