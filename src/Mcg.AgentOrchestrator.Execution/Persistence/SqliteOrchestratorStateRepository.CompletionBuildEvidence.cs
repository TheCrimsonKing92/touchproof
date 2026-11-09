using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class SqliteOrchestratorStateRepository
{
    private static CompletionBuildEvidenceOperation? MergeCompletionBuildEvidence(
        CompletionBuildEvidenceOperation? baseline, CompletionBuildEvidenceOperation? stored,
        CompletionBuildEvidenceOperation? current)
    {
        if (stored is null) return current;
        if (current is null) return stored;
        if (stored.DispatchAt != current.DispatchAt)
            return stored.DispatchAt > current.DispatchAt ? stored : current;
        if (TaskSpec.IsRelaunchRecoveryOf(stored, current)) return current;
        if (TaskSpec.IsRelaunchRecoveryOf(current, stored)) return stored;
        // All other competing operations retain the first durable identity.
        if (stored.Id != current.Id) return stored;
        if (stored.Applied || stored.Phase == CompletionBuildEvidencePhase.Cancelled) return stored;
        if (!stored.IsPending)
        {
            // Current-task receipt application may advance; a stale cancellation cannot
            // rewrite the independently persisted terminal build fact or custody marker.
            return current.Applied && current.Phase == stored.Phase && current.HasSameOrigin(stored) &&
                CompletionBuildEvidenceTarget.From(current).Matches(stored)
                ? stored with { Applied = true } : stored;
        }
        if (current.Applied || current.Phase == CompletionBuildEvidencePhase.Cancelled) return current;
        // An unchanged tick snapshot must not undo progress checkpointed by the operation owner.
        if (SnapshotEquals(baseline, current)) return stored;
        // Even a changed diagnostic cannot move an authorized child back into the launch path.
        if (stored.ChildPid is not null && current.ChildPid is null && current.IsPending) return stored;
        return current.Phase == CompletionBuildEvidencePhase.Launching ? stored : current;
    }

}
