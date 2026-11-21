namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record SourceBacklogContinuationLineage(
    string BacklogItemId,
    string PredecessorGoalId,
    string SuccessorGoalId,
    long ClaimVersionBefore,
    long ClaimVersionAfter,
    DateTimeOffset TransferredAt);
