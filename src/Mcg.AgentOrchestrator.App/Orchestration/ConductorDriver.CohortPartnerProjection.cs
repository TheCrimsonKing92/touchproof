using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal GateReadyProjectionCache ProjectionCache { get; } = new();
    internal PreLandingMergeConflictProbeCache MergeConflictCache { get; } = new();
    internal PassedCohortApprovalHoldEvaluationCache ApprovalHoldCache { get; } = new();
    internal Func<Goal, PreLandingMergeConflictProbeCache.Key?>? PreLandingMergeConflictKeyReader { get; set; }
    internal Func<Goal, GateReadyProjectionCache.Heads?>? GateReadyProjectionHeadsReader { get; set; }
    internal AdmissionFactsDriverState AdmissionFacts { get; } = new();
    internal void BeginAdmissionFacts(IEnumerable<Goal> goals, AgentOrchestratorKernel kernel) =>
        AdmissionFacts.Begin(goals, kernel, ExecutionDirectory, _integrationBranch, _cohortWorkspace,
            _cohortAcceptanceStore, HasFinalizedCohortLanding);

    internal GateReadyCandidateProjectionResult ProjectGroupedAdmissionCandidate(Goal goal, ConductorAutonomyPolicy policy, AgentOrchestratorKernel? kernelArgument) =>
        AdmissionFacts.ApplyApproval(goal, GroupedAdmissionLandingHold.Apply(ProjectCachedGateReadyCandidate(goal, policy),
            (_cohortKernel ?? kernelArgument ?? _conductorTickKernel)?.GetPendingHumanInput(goal.Id) ?? [], goal), ExecutionDirectory);

    internal GateReadyCandidateProjectionResult ProjectCachedGateReadyCandidate(Goal goal, ConductorAutonomyPolicy policy)
    {
        if (HasActiveOwnerReviewHold(goal, out _, out _)) return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.OwnerReviewHold);
        if (HasActiveApparatusHold(goal, out _)) return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.ApparatusHold);
        return AdmissionFacts.Project(goal, policy, ProjectionCache, GetFacts, _isVerificationGateSatisfied,
            () => ProjectGateReadyCandidate(goal, policy), GateReadyProjectionHeadsReader,
            _cohortWorkspace, ExecutionDirectory, _integrationBranch);
    }

    // This projection judges pair compatibility only. It never makes the in-review goal gate-ready.
    internal GateReadyCandidateProjectionResult ProjectInReviewCohortPartner(Goal goal, ConductorAutonomyPolicy policy)
    {
        if (HasActiveOwnerReviewHold(goal, out _, out _))
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.OwnerReviewHold);

        if (HasActiveApparatusHold(goal, out _))
            return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.ApparatusHold);

        try
        {
            var risk = _classifyChangeRisk(goal);
            if (risk is null)
                return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.RiskUnknown);

            return _gateReadyCandidateProjector?.Project(new GateReadyCandidateInput(
                goal.Id,
                GoalLifecycleState.Verified,
                true,
                risk,
                policy.GetTransitionDecision(GoalLifecycleState.Merged, risk.Value)))
                ?? ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.RevisionUnknown);
        }
        catch { return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.RevisionUnknown); }
    }
}
