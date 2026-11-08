using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ExecutorSlotConcurrencyAttribution
{
    internal static IReadOnlyList<ExecutorSlotSample> Build(IEnumerable<RemoteExecutorOutcomeRow> outcomes,
        string executorId, DateTimeOffset since, DateTimeOffset now)
    {
        var rows = outcomes.Where(row => !string.IsNullOrWhiteSpace(row.ExecutorId) && row.ExecutorId == executorId &&
            row.Outcome is not ("not-eligible-exclusive-resource" or "late-after-fallback" or "executor-busy" or
                "preempted-by-gate" or "executor-tools-mismatch") && row.ObservedAt <= now).ToArray();
        var selected = rows.Where(row => row.ObservedAt >= since).ToArray();
        var earliest = selected.Where(row => row.StartedAt is { } start && start < row.ObservedAt)
            .Select(row => row.StartedAt!.Value).DefaultIfEmpty(since).Min();
        var timed = rows.Where(row => row.ObservedAt > earliest && row.StartedAt is { } start && start < row.ObservedAt).ToArray();
        // Calculate in-flight counts once per distinct start; reuse them for each sample's interval peak.
        var peaks = timed.Select(row => row.StartedAt!.Value).Distinct().Order().Select(at =>
            (At: at, Count: timed.Count(other => other.StartedAt <= at && at < other.ObservedAt))).ToArray();
        return selected.OrderBy(row => row.ObservedAt)
            .ThenBy(row => row.GateAttemptId, StringComparer.Ordinal).Select(row =>
            {
                int? k = null;
                if (row.StartedAt is { } start && start < row.ObservedAt)
                {
                    // Peak over THIS interval, including jobs that start after this job. Touching ends do not overlap.
                    k = Math.Clamp(peaks.Where(peak => peak.At >= start && peak.At < row.ObservedAt)
                        .Max(peak => peak.Count), 1, 4);
                }
                return new ExecutorSlotSample(row.GateAttemptId, row.Lane, row.ObservedAt, row.StartedAt, k,
                    row.Outcome, row.RemoteSeconds);
            }).ToArray();
    }
}
