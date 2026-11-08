namespace Mcg.AgentOrchestrator.Infrastructure;

// The decision sorts by this same pair. A completed window cannot count again,
// even when several terminal attempts share a timestamp.
internal sealed record ExecutorSlotWatermark(DateTimeOffset ObservedAt, string AttemptId)
{
    internal bool Precedes(ExecutorSlotSample sample) => sample.ObservedAt > ObservedAt ||
        (sample.ObservedAt == ObservedAt && StringComparer.Ordinal.Compare(sample.AttemptId, AttemptId) > 0);
}
