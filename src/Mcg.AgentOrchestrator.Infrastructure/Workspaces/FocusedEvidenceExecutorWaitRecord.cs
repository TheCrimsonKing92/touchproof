namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceExecutorWaitRecord(string Lane, DateTimeOffset Started, DateTimeOffset Ended,
    TimeSpan Elapsed, TimeSpan Bound, string Outcome, string? ExecutorId);
