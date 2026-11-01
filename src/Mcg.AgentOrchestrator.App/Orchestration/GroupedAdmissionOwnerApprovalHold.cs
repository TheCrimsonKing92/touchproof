using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Grouping must not make another goal depend on this candidate's owner decision.
internal static class GroupedAdmissionOwnerApprovalHold
{
    internal static GateReadyCandidateProjectionResult Apply(
        GateReadyCandidateProjectionResult projection, Goal goal, string? executionDirectory)
    {
        if (projection is not GateReadyCandidateProjectionResult.Ready ready || executionDirectory is null)
            return projection;

        LandingDenylist denylist;
        try
        {
            denylist = LandingExecutor.LoadLandingDenylist(executionDirectory, ready.Projection.MainRevision).Denylist;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // An unavailable observation cannot establish an owner-approval hold. Landing still
            // owns its independent, fail-closed policy check before any mutation.
            return projection;
        }

        var decisions = CollaborationItemStore.ForDirectory(
            OrchestratorWorkspace.ForDirectory(executionDirectory).OrchestratorDirectory);
        var approval = LandingDenylistOwnerGate.Evaluate(executionDirectory, denylist, decisions,
            goal.Id.Value, ready.Projection.MainRevision, ready.Projection.CandidateRevision, ready.Projection.LandingPaths);
        return approval.Approved ? projection :
            new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.LandingHeld)
            {
                HoldRequestId = AcceptancePolicyChangeDecision.RequestId(goal.Id.Value, ready.Projection.CandidateRevision),
                HoldReason = approval.HoldReason
            };
    }

    internal static void ReportExclusions(IEnumerable<ConductorSpeculativeAcceptanceCandidate> candidates,
        int tick, Action<string> recordProgress)
    {
        foreach (var candidate in candidates)
            if (candidate.ProjectionResult is GateReadyCandidateProjectionResult.Excluded
                { Reason: GateReadyCandidateExclusionReason.LandingHeld, HoldReason: { } reason })
                recordProgress($"GROUPED_ADMISSION_HELD tick={tick} goal={candidate.GoalId.Value[..8]} reason={reason}");
    }
}
