namespace Mcg.AgentOrchestrator.App.Orchestration;

// A live grouped attempt retaining its acceptance slot; reservations alone have no pending step.
internal sealed record ActivationAdmissionSlotOwner(
    ActivationAdmissionPreparingOwner Identity,
    IReadOnlyList<string> MemberGoalIds,
    int ProcessId,
    IReadOnlyList<string> RunKeys,
    ActivationAdmissionOwnerPendingStep PendingStep);
