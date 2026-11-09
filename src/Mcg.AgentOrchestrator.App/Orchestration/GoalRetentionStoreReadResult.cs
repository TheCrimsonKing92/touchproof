using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record GoalRetentionStoreReadResult(
    IReadOnlyList<StoredGoalRetentionFact> ReadableFacts,
    int ReadableStoreCount,
    IReadOnlyList<UnavailableGoalStore> Unavailable);
