namespace Mcg.AgentOrchestrator.Core;

public static class RefinedSpecSnapshotAccess
{
    public static RefinedSpecSnapshot? GetCurrent(GoalSnapshot snapshot) =>
        UsesLegacyTopLevel(snapshot.RefinedSpecVersions?.Count ?? 0)
            ? snapshot.RefinedSpec
            : snapshot.RefinedSpecVersions!.LastOrDefault(version => version.SupersededByVersion is null)?.Spec;

    public static bool UsesLegacyTopLevel(int versionCount) => versionCount == 0;

    public static GoalSnapshot NormalizeForStorage(GoalSnapshot snapshot) =>
        snapshot.RefinedSpec is not null && !UsesLegacyTopLevel(snapshot.RefinedSpecVersions?.Count ?? 0)
            ? snapshot with { RefinedSpec = null }
            : snapshot;
}
