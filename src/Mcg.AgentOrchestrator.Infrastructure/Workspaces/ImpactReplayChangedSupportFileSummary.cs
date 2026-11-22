namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ImpactReplayChangedSupportFileSummary(
    string Path, int DirectMatchCount, int ClosureCount, bool Complete, bool Truncated, bool Unresolved,
    IReadOnlyList<string> AffectedProjects, bool FullRunTriggered, IReadOnlyList<string> Reasons);

internal sealed record ImpactReplayChangedSupportSelection(
    IReadOnlyList<string> Classes, string? AbandonReason, IReadOnlyList<string> OtherFiles,
    IReadOnlyList<ImpactReplayChangedSupportFileSummary> SupportSelections,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectClasses)
{
    internal static ImpactReplayChangedSupportSelection Empty { get; } = new([], null, [], [],
        new Dictionary<string, IReadOnlyList<string>>());

    internal static ImpactReplayChangedSupportSelection Incomplete(IEnumerable<string> changedFiles,
        IReadOnlyList<string> projects, string reason, int threshold) => Empty with
    {
        AbandonReason = reason,
        SupportSelections = changedFiles.Order(StringComparer.Ordinal)
            .Select(path => new ImpactReplayChangedSupportFileSummary(path, 0, 0, false, false, true, projects, true,
                threshold <= 0 ? [reason, "invalid support closure threshold"] : [reason])).ToArray()
    };
}
