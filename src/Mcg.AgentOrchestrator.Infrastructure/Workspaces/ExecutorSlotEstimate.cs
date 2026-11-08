namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ExecutorSlotEstimate(int K, int Samples, double? Slowdown, double? Throughput,
    ExecutorSlotFailureRates Failures, IReadOnlyList<ExecutorSlotLaneEstimate> Lanes);
