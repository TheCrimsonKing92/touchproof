namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ExecutorSlotDecisionRecord(string EvaluationId, DateTimeOffset ObservedAt,
    string ExecutorId, ExecutorSlotControllerSettings Settings, ExecutorSlotDecisionResult Decision,
    ExecutorSlotControllerState BeforeState, string ApplicationStatus, string AuditStatus = "pending",
    int Version = 1);
