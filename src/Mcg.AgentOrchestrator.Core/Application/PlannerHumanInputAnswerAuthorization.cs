namespace Mcg.AgentOrchestrator.Core;

// Owns the narrow host-receipt exception to explicit Operator answers for typed Planner waits.
internal static class PlannerHumanInputAnswerAuthorization
{
    // Ordinary Worker submissions cannot provide this host-only receipt validator.
    internal static void SubmitHumanInput(this AgentOrchestratorKernel kernel,
        HumanInputRequestId requestId, string answer, Func<int, bool> hasCurrentSelectorlessGateReceipt)
        => kernel.SubmitHumanInputCore(requestId, answer, null, HumanInputAnswerOrigin.Worker,
            supersedeSiblings: false, isAutoDefaulted: false,
            hasCurrentSelectorlessGateReceipt: hasCurrentSelectorlessGateReceipt);

    internal static bool IsAuthorized(HumanInputRequest request, Goal goal,
        HumanInputAnswerOrigin origin, bool supersedeSiblings, bool isAutoDefaulted,
        Func<int, bool>? hasCurrentSelectorlessGateReceipt)
        => request.PlannerIdentity is null || origin == HumanInputAnswerOrigin.Operator ||
           (origin == HumanInputAnswerOrigin.Worker && !supersedeSiblings && !isAutoDefaulted &&
            request.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
            GateTestObligation.IsAcceptanceGateOwner(request.EvidenceOwner) &&
            request.PlannerEvidence?.Availability == "post-implementation" &&
            request.TestNames is null or { Count: 0 } &&
            request.IsCurrentForBrief(goal.AuthoritativeBrief.Version) &&
            hasCurrentSelectorlessGateReceipt?.Invoke(goal.AuthoritativeBrief.Version) == true);
}
