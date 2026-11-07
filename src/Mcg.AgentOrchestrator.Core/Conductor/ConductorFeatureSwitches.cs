namespace Mcg.AgentOrchestrator.Core.Conductor;

// Immutable feature enablement for one conductor generation, resolved at startup.
public sealed record ConductorFeatureSwitches(
    bool Steward = true,
    bool Author = true,
    bool SelfRelaunch = true,
    bool MergeTrains = true,
    bool ArchivedSnapshotCompression = false)
{
    public static ConductorFeatureSwitches Default { get; } = new();

    public string ToLogFields() =>
        $"features.steward={Steward.ToString().ToLowerInvariant()} " +
        $"features.author={Author.ToString().ToLowerInvariant()} " +
        $"features.selfRelaunch={SelfRelaunch.ToString().ToLowerInvariant()} " +
        $"features.mergeTrains={MergeTrains.ToString().ToLowerInvariant()} " +
        $"features.archivedSnapshotCompression={ArchivedSnapshotCompression.ToString().ToLowerInvariant()}";
}
