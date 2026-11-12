using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ActivationAdmissionPreparingOwnerCensus
{
    internal static IReadOnlyList<ActivationAdmissionPreparingOwner> Evaluate(
        IReadOnlyList<ActivationAdmissionSlotOwner> owners,
        IReadOnlyList<GateLoadContextProbe.LiveGateOccupant> gates) =>
        owners.Where(owner => owner.PendingStep != ActivationAdmissionOwnerPendingStep.None &&
            !gates.Any(gate => gate.CountsAsAcceptanceOccupant && Represents(gate, owner)))
            .Select(owner => owner.Identity).Distinct().ToArray();

    private static bool Represents(GateLoadContextProbe.LiveGateOccupant gate,
        ActivationAdmissionSlotOwner owner) =>
        (gate.ProcessId is > 0 && gate.ProcessId.Value == owner.ProcessId) ||
        (!string.IsNullOrWhiteSpace(gate.GoalId) &&
            owner.MemberGoalIds.Contains(gate.GoalId, StringComparer.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(gate.RunId) &&
            owner.RunKeys.Contains(gate.RunId.Trim(), StringComparer.OrdinalIgnoreCase));
}
