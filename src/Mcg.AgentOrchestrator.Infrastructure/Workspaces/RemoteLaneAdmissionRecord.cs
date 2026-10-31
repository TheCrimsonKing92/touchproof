namespace Mcg.AgentOrchestrator.Infrastructure;

// Frozen with the slot reservation; concurrent names exclude this lane.
internal sealed record RemoteLaneAdmissionRecord(string ExecutorId, int Concurrency,
    IReadOnlyList<string> ConcurrentLanes, double ReservedCost, int CostSamples, string CostClass,
    double? CostBudget, double TotalReservedCost);
