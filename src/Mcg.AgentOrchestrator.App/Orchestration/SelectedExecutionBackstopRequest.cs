namespace Mcg.AgentOrchestrator.App.Orchestration;

// RetryPending means the last attempt completed with infrastructure failure and has rejoined the queue.
internal sealed record SelectedExecutionBackstopRequest(string Id, string FromCommit, string ToCommit,
    long ThroughOrdinal, IReadOnlyList<string> GoalIds, DateTimeOffset QueuedAt,
    int Overtakes = 0, int Attempts = 0, bool RetryPending = false);
