namespace Mcg.AgentOrchestrator.Infrastructure;

// Ordinary evaluations serialize one checkpoint. Pending transitions also retain the
// committed state until the observed setting proves the proposal was applied.
internal sealed record ExecutorSlotDecisionRecord(string EvaluationId, DateTimeOffset ObservedAt,
    string ExecutorId, ExecutorSlotControllerSettings Settings, ExecutorSlotDecisionResult Decision,
    ExecutorSlotControllerState ControllerState, string ApplicationStatus, string AuditStatus = "pending",
    int Version = 3,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ExecutorSlotControllerState? BeforeTransitionState = null);
