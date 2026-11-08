namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ExecutorSlotLaneEstimate(string Lane, int SoloSamples, int Samples, int Jobs,
    double? SoloMedianSeconds, double? MedianSeconds, double? Slowdown, double Weight, string? Exclusion);
