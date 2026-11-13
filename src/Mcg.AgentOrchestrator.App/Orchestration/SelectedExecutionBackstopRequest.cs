namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SelectedExecutionBackstopRequest(string Id, string FromCommit, string ToCommit,
    long ThroughOrdinal, IReadOnlyList<string> GoalIds, DateTimeOffset QueuedAt,
    int Overtakes = 0, int Attempts = 0);
