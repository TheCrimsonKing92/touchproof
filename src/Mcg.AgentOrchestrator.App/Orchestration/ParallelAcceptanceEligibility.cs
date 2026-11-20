using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ParallelAcceptanceEligibility
{
    internal static string? GetDeclineReason(
        Goal goal,
        ConductorTransitionDecision verifiedTransition,
        Func<Goal, bool> hasRoutableCohortFailure,
        Func<Goal, GoalLifecycleFacts> getFacts,
        Func<Goal, bool> hasAcceptanceHold)
    {
        if (MultiRepositoryGoal.Applies(goal)) return MultiRepositoryGoal.Reason(goal);
        if (goal.Status == GoalStatus.Parked) return CandidateDeclineHoldActive;
        if (hasRoutableCohortFailure(goal)) return CandidateDeclineCohortAttributionFailure;

        if (verifiedTransition == ConductorTransitionDecision.Escalate)
            return CandidateDeclineVerifiedTransitionRequiresOperator;

        if (GoalLifecycle.ResolveState(goal, getFacts(goal)) is not (GoalLifecycleState.Verified or GoalLifecycleState.Verifying))
            return CandidateDeclineLifecycleNotVerified;

        if (!AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal))
            return CandidateDeclineVerificationIncomplete;

        return hasAcceptanceHold(goal) ? CandidateDeclineHoldActive : null;
    }

    internal static void ValidateSlot(
        Goal goal, int slotIndex, Func<string, bool> hasLiveAttempt,
        Func<Goal, ReconcileAcceptanceLeaseState?> readActiveLease,
        Func<Goal, int> getSlotCount)
    {
        if (!hasLiveAttempt(goal.Id.Value) && readActiveLease(goal) is { } lease)
            throw new EvidenceMutationLeaseUnavailableException(FormatEvidenceMutationLeaseHeld(lease));

        var slotCount = getSlotCount(goal);
        if (slotIndex < 0 || slotIndex >= slotCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotIndex),
                slotIndex,
                $"Acceptance slot index must be 0 through {slotCount - 1} for goal {goal.Id.Value[..8]}.");
        }
    }
}
