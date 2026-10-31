using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PassedGateRegateAdmission
{
    internal static PassedGateHold? Resolve(Goal goal, ConductorParallelAcceptanceAttempt? attempt, string? candidate)
    {
        if (goal.Status != GoalStatus.Verified || goal.LandingBlocker is null ||
            string.IsNullOrWhiteSpace(candidate) || attempt is not
            { Outcome: ConductorParallelAcceptanceAttemptOutcome.Passed, ReconciledAt: not null,
              SupersededBy: null, ReplayLandedAt: null, RegateRequestedAt: null } ||
            attempt.GoalId != goal.Id.Value ||
            attempt.Kind != ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind ||
            !string.Equals(attempt.BranchHeadSha, candidate, StringComparison.OrdinalIgnoreCase)) return null;
        return PassedGateHoldClassifier.Classify(goal, attempt.AttemptId, candidate) is { } hold
            ? hold with { ReceiptPath = attempt.ResultPath } : null;
    }
}
