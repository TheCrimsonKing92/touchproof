namespace Mcg.AgentOrchestrator.Infrastructure;

// Exactly one checkpoint is serialized. Pending-handler checkpoints are proposals;
// Restore accepts them only when the observed slot setting matches the target.
internal sealed record ExecutorSlotDecisionRecord(string EvaluationId, DateTimeOffset ObservedAt,
    string ExecutorId, ExecutorSlotControllerSettings Settings, ExecutorSlotDecisionResult Decision,
    ExecutorSlotControllerState ControllerState, string ApplicationStatus, string AuditStatus = "pending",
    int Version = 2);
