using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PassedCohortReceiptPartnerRelease
{
    // Observe the exact passed receipt before applying its owner-approval exclusions. This is
    // only an observation: production grouped candidates and the landing gate remain unchanged.
    internal static GateReadyCandidateProjectionResult ProjectReceiptCandidate(
        ConductorDriver driver, Goal goal, ConductorAutonomyPolicy policy, AgentOrchestratorKernel kernel) =>
        GroupedAdmissionLandingHold.Apply(driver.ProjectGateReadyCandidate(goal, policy),
            kernel.GetPendingHumanInput(goal.Id), goal);

    internal static IReadOnlyList<GoalId> HeldMembers(PassedCohortReceiptApprovalHold.Decision hold,
        int tick, Action<string> recordProgress)
    {
        var held = hold.MissingApprovals.Select(item => item.GoalId).ToHashSet();
        var released = hold.Receipt.Identity.Members.Where(member => !held.Contains(member.GoalId)).ToArray();
        // A receipt binds its entire member set and tested tree. Partners re-enter ordinary
        // admission; none can land a subset using this receipt.
        if (released.Length > 0)
            recordProgress($"COHORT_RECEIPT_RELEASED tick={tick} receipt={hold.Receipt.ReceiptId} " +
                $"reason=landing-denylist-owner-approval held={string.Join(',', held.OrderBy(id => id.Value, StringComparer.Ordinal).Select(id => id.Value[..8]))} " +
                $"released={string.Join(',', released.Select(member => member.GoalId.Value[..8]))} path=requeue");
        return hold.MissingApprovals.Select(item => item.GoalId).ToArray();
    }
}
