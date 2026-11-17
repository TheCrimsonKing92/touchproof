namespace Mcg.AgentOrchestrator.Core;

internal static class DispatchSnapshotShaping
{
    internal static IReadOnlyDictionary<int, string> BriefLookup(IReadOnlyList<GoalBriefVersion>? versions)
    {
        // Ambiguous versions cannot authorize removing the inline copy.
        return (versions ?? []).GroupBy(version => version.Version)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Text);
    }

    internal static TaskDispatchSnapshot ForStorage(
        TaskDispatchSnapshot snapshot, bool isNewest, IReadOnlyDictionary<int, string> briefVersions)
    {
        if (snapshot.BriefSnapshot is not null &&
            briefVersions.TryGetValue(snapshot.BriefVersion, out var text) &&
            string.Equals(snapshot.BriefSnapshot, text, StringComparison.Ordinal))
        {
            snapshot = snapshot with { BriefSnapshot = null, BriefSnapshotFromVersion = true };
        }

        if (!isNewest && snapshot.ContextPackageReceipt is { } receipt)
        {
            snapshot = snapshot with
            {
                ContextPackageReceipt = receipt with
                {
                    SectionCount = receipt.Sections.Count == 0 && receipt.SectionCount.HasValue
                        ? receipt.SectionCount : receipt.Sections.Count,
                    Sections = []
                }
            };
        }

        return snapshot;
    }

    internal static TaskDispatchSnapshot FromStorage(
        TaskDispatchSnapshot snapshot, IReadOnlyDictionary<int, string>? briefVersions)
    {
        // Null without the marker is genuine absence, including in legacy rows.
        return snapshot.BriefSnapshot is null && snapshot.BriefSnapshotFromVersion == true &&
            briefVersions is not null && briefVersions.TryGetValue(snapshot.BriefVersion, out var text)
                ? snapshot with { BriefSnapshot = text, BriefSnapshotFromVersion = null }
                : snapshot;
    }
}
