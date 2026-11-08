namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ExecutorSlotTrial(string Id, int PriorSlots, int TargetSlots,
    DateTimeOffset StartedAt, ExecutorSlotWindow Baseline);
