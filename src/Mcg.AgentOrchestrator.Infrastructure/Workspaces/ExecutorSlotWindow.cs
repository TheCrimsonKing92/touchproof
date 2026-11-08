namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ExecutorSlotWindow(string Id, IReadOnlyList<string> AttemptIds,
    DateTimeOffset EndedAt, ExecutorSlotEstimate Estimate);
