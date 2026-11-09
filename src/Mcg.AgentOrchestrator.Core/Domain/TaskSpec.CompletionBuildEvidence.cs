namespace Mcg.AgentOrchestrator.Core;

public sealed partial class TaskSpec
{
    internal void RecordCompletionBuildEvidence(CompletionBuildEvidenceOperation operation)
    {
        var previous = CompletionBuildEvidence;
        var attachedOrigin = previous is not null && previous.HasSameOrigin(operation) &&
            DispatchHistory.Any(dispatch => dispatch.DispatchedAt == operation.DispatchAt);
        if (operation.TaskId != Id.Value ||
            (operation.DispatchAt != LastDispatch?.DispatchedAt && !attachedOrigin))
            throw new InvalidOperationException("Completion build evidence does not belong to the current dispatch.");
        var recovery = Status == WorkTaskStatus.Failed && RequiredRole is AgentRole.Developer or AgentRole.Tester &&
            previous is not null && IsRelaunchRecoveryOf(previous, operation);
        if (previous is not null && !recovery && (!previous.HasSameOrigin(operation) ||
            (previous.ChildPid is not null && !CompletionBuildEvidenceTarget.From(previous).Matches(operation))))
            throw new InvalidOperationException("Completion build evidence cannot replace attached operation identity.");
        if (previous is not null && !recovery &&
            (!previous.IsPending && operation.IsPending || previous.Applied && !operation.Applied ||
             previous.CustodyReleased && !operation.CustodyReleased ||
             !previous.IsPending && previous.Phase != operation.Phase &&
             !(previous.Phase == CompletionBuildEvidencePhase.Success && operation.Phase == CompletionBuildEvidencePhase.StaleCandidate)))
            throw new InvalidOperationException("Completion build evidence cannot move backwards.");
        CompletionBuildEvidence = operation;
    }

    // A named recovery is the sole exception to one operation per dispatch. Lifecycle fields
    // may already have advanced when a second kernel adopts the durable winner.
    public static bool IsRelaunchRecoveryOf(CompletionBuildEvidenceOperation original,
        CompletionBuildEvidenceOperation recovery) =>
        original is { Phase: CompletionBuildEvidencePhase.ApparatusFailure, Applied: true, CustodyReleased: true, RecoversOperationId: null } &&
        recovery.RecoversOperationId == original.Id && recovery.Id != original.Id &&
        // Only the recovery generation's own launch authority changes; reuse the full candidate/worker identity check.
        original.HasSameOrigin(recovery with
        {
            Id = original.Id, RecoversOperationId = null, ResultPath = original.ResultPath,
            Deadline = original.Deadline, LaunchOwnerPid = original.LaunchOwnerPid,
            LaunchOwnerStartedAt = original.LaunchOwnerStartedAt
        });
}
