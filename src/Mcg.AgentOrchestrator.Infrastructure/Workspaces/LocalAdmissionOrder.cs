using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Local admission protects checks that configured remote executors cannot serve.
// Capacity and outcome recording belong to the coordinator, not this ordering policy.
internal static class LocalAdmissionOrder
{
    internal static bool IsLocalBound(Check check, IReadOnlyList<string>? remoteEligibleKeys) =>
        remoteEligibleKeys is not null && (check.RequiresBuildSystemChange ||
            RemoteLaneResourceEligibility.ExclusionReason(check.ExclusiveResourceKeys,
                check.RequiresBuildSystemChange, remoteEligibleKeys) is not null);

    internal static IEnumerable<(Check Check, int Index)> Order(
        IReadOnlyList<Check> checks, IReadOnlyList<string>? remoteEligibleKeys) => checks
        .Select((check, index) => (Check: check, Index: index))
        .OrderByDescending(shard => IsLocalBound(shard.Check, remoteEligibleKeys))
        .ThenByDescending(shard => AcceptanceLaneDurationStore.ResolveSortSeconds(shard.Check))
        .ThenBy(shard => shard.Index);
}
