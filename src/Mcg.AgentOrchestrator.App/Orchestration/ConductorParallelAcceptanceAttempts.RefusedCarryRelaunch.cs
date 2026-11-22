using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorParallelAcceptanceAttemptCoordinator
{
    private bool TryHoldRefusedCarryRelaunch(
        ConductorParallelAcceptanceAttempt current,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        string dispatchKind,
        out ConductorParallelAcceptanceAttemptDecision decision)
    {
        decision = null!;
        if (current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed ||
            !string.Equals(dispatchKind, GateDispatchKind, StringComparison.Ordinal))
            return false;

        var hold = EvaluateRefusedCarryAdmission(candidate.Goal, candidate.BranchHeadSha, candidate.MainHeadSha,
            policy, _executionDirectory, current);
        if (hold is null) return false;

        decision = ConductorParallelAcceptanceAttemptDecision.Completed(
            current,
            ConductorParallelAcceptanceRunResult.Early(candidate, hold));
        return true;
    }

    internal ConductorAdvanceResult? EvaluateRefusedCarryAdmission(
        Goal goal, string? branchSha, string? mainSha, ConductorAutonomyPolicy policy, string? executionDirectory,
        ConductorParallelAcceptanceAttempt? latest = null)
    {
        if (string.IsNullOrWhiteSpace(branchSha) || string.IsNullOrWhiteSpace(mainSha))
            return null;
        var stale = AcceptanceCriterionEvidence.StaleFullGateBindings(goal, branchSha);
        if (stale.Length == 0) return null;

        var history = string.IsNullOrWhiteSpace(executionDirectory)
            ? [] : GoalOperationJournal.ReadRefusedCarryHolds(executionDirectory, goal.Id).ToArray();
        var liveBindings = stale.Select(AcceptanceCriterionEvidence.BindingOf).ToHashSet();
        var prior = history.FirstOrDefault(note => note.Bindings.Any(liveBindings.Contains));
        var passedBranch = prior?.BranchSha;
        var passedMain = prior?.MainSha;
        CriterionEvidenceObligation[]? inferredBindings = null;
        if (prior is null)
        {
            // Earlier attempts may have reached the refusal before admission journaled it.
            // Re-derive it against the original branch and the current binding contract.
            latest ??= TryReadLatest(goal.Id.Value);
            if (latest is not { Kind: GateDispatchKind, Outcome: ConductorParallelAcceptanceAttemptOutcome.Passed,
                    ReconciledAt: not null, SupersededBy: null, RegateRequestedAt: null, ReplayLandedAt: null,
                    BranchHeadSha: { Length: > 0 }, MainHeadSha: { Length: > 0 } } ||
                history.Any(note => string.Equals(note.BranchSha, latest.BranchHeadSha, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(note.MainSha, latest.MainHeadSha, StringComparison.OrdinalIgnoreCase)) ||
                (latest.ReplayContextFingerprint is not null && !HasReplayProof(latest, goal)))
                return null;
            passedBranch = latest.BranchHeadSha;
            passedMain = latest.MainHeadSha;
            if (string.IsNullOrWhiteSpace(executionDirectory) &&
                (!string.Equals(branchSha, passedBranch, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(mainSha, passedMain, StringComparison.OrdinalIgnoreCase)))
                return null;
            inferredBindings = AcceptanceCriterionEvidence.StaleFullGateBindings(goal, passedBranch)
                .Where(item => liveBindings.Contains(AcceptanceCriterionEvidence.BindingOf(item))).ToArray();
            if (inferredBindings.Length == 0 ||
                (!string.Equals(branchSha, passedBranch, StringComparison.OrdinalIgnoreCase) &&
                 !AcceptanceCriterionEvidence.IsFullGateCarryRefused(goal, passedBranch!, _integrationBranch, executionDirectory, out _)))
                return null;
        }

        if (!AcceptanceCriterionEvidence.IsFullGateCarryRefused(goal, branchSha, _integrationBranch, executionDirectory, out var refusal))
            return null;
        var hold = AcceptanceCriterionEvidence.DescribeRefusedCarryHold(
            goal, branchSha, mainSha, passedBranch!, passedMain, stale, refusal);
        if (inferredBindings is not null)
            GoalOperationJournal.RecordRefusedCarryHold(executionDirectory, goal, passedBranch!, passedMain,
                inferredBindings, hold.Reason, latest!.AttemptId);
        return new ConductorAdvanceResult(goal.Id.Value, goal.Id.Value[..8], policy.Name, hold);
    }
}
