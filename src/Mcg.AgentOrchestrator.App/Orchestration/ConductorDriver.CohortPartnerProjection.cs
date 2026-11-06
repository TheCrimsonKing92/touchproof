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

    internal GateReadyCandidateProjectionResult ProjectGroupedAdmissionCandidate(Goal goal, ConductorAutonomyPolicy policy, AgentOrchestratorKernel? kernelArgument) =>
        GroupedAdmissionCandidateProjection.Project(ProjectCachedGateReadyCandidate(goal, policy), goal,
            (_cohortKernel ?? kernelArgument ?? _conductorTickKernel)?.GetPendingHumanInput(goal.Id) ?? [], ExecutionDirectory);

    internal GateReadyCandidateProjectionResult ProjectCachedGateReadyCandidate(Goal goal, ConductorAutonomyPolicy policy)
    {
        if (HasActiveOwnerReviewHold(goal, out _, out _)) return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.OwnerReviewHold);
        if (HasActiveApparatusHold(goal, out _)) return ExcludedGateReadyCandidate(GateReadyCandidateExclusionReason.ApparatusHold);
        return ProjectionCache.GetOrProject(goal.Id, ReadKey(),
            () => ProjectGateReadyCandidate(goal, policy), ReadKey);

        GateReadyProjectionCache.Key? ReadKey()
        {
            try
            {
                var heads = GateReadyProjectionHeadsReader is { } read ? read(goal) : ReadProjectionHeads(goal);
                return heads is null ? null : GateReadyProjectionCache.CreateKey(goal, policy, heads, GetFacts(goal));
            }
            catch { return null; } // Missing refs/facts bypass reuse; the original projection owns its failure result.
        }
    }

    private GateReadyProjectionCache.Heads? ReadProjectionHeads(Goal goal)
    {
        if (_cohortWorkspace is null || ExecutionDirectory is null) return null;
        var worktree = GoalWorktrees.TryResolve(ExecutionDirectory, goal.Id);
        if (worktree is null) return null;
        var pair = ConductorGitRevisionReader.ReadRequiredPair(worktree, _integrationBranch);
        return new(pair.BranchRevision!, pair.MainRevision!,
            ConductorGitRevisionReader.ReadRequiredCommit(ExecutionDirectory, "HEAD"));
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
