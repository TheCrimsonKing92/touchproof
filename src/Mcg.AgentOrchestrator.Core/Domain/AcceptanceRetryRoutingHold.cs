namespace Mcg.AgentOrchestrator.Core;

// Provenance of one automatic acceptance retry, including a snapshotted routing deadline.
public sealed record AcceptanceRetryRoutingHold(
    DateTimeOffset RoundRetryAt,
    DateTimeOffset FailureRecordedAt,
    DateTimeOffset Deadline,
    int HoldSeconds,
    string? AttemptId,
    IReadOnlyList<string> AutomaticFeedback);
