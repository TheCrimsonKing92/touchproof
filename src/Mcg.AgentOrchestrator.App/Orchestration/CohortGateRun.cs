namespace Mcg.AgentOrchestrator.App.Orchestration;

// A running cohort binds its member set and fingerprint to one completion signal.
internal sealed record CohortGateRun(
    DateTimeOffset StartedAt,
    IReadOnlySet<string> MemberGoalIds,
    string PairFingerprint,
    TaskCompletionSource Completion,
    string? AttemptMetadataPath = null);
