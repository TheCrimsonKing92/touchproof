namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ExecutorSlotSample(string AttemptId, string Lane, DateTimeOffset ObservedAt,
    DateTimeOffset? StartedAt, int? Concurrency, string Outcome, double? RemoteSeconds,
    string IntervalSource = "reported");
