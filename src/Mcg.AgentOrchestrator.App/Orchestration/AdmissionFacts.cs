using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AdmissionFacts(AdmissionFactsInputs Inputs, GateReadyLandingScopeObservation Scope,
    GateReadyMergeTreeObservation Merge, ChangeRiskTier? Risk, LandingDenylistOwnerGate.Result Approval,
    AdditiveConflictProbeResult? ConflictProbe)
{
    internal GateReadyCandidateProjector Projector() => new(
        _ => new(Inputs.Branch, Inputs.Main), _ => Scope, (_, _, _) => Merge);

    internal GateReadyCandidateProjectionResult ApplyApproval(GateReadyCandidateProjectionResult projection) =>
        projection is GateReadyCandidateProjectionResult.Ready && !Approval.Approved
            ? new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.LandingHeld)
            {
                HoldRequestId = AcceptancePolicyChangeDecision.RequestId(Inputs.GoalId.Value, Inputs.Branch),
                HoldReason = Approval.HoldReason
            } : projection;
}
