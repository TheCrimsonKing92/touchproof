using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Accepted durations from the bounded history tail, grouped by exact key and lane name.
internal static class RemoteLaneOfferHistoryReader
{
    internal const int LineLimit = 10_000;

    internal static IReadOnlyDictionary<(string Lane, string Filter), RemoteExecutorLaneSeconds> Read(string path) =>
        ReadWithLaneNames(path).Exact;

    internal static (IReadOnlyDictionary<(string Lane, string Filter), RemoteExecutorLaneSeconds> Exact,
        RemoteLaneNameDurationHistory LaneNames) ReadWithLaneNames(string path)
    {
        var rows = new List<RemoteExecutorOutcomeRow>();
        var keys = new Dictionary<string, (string Lane, string Filter)>(StringComparer.Ordinal);
        try
        {
            // Retain and parse only the tail, including malformed and non-accepted lines in the limit.
            foreach (var line in SharedJsonlFile.ReadLines(path).TakeLast(LineLimit))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var outcome = root.GetProperty("outcome").GetString();
                    if (outcome is null or "not-eligible-exclusive-resource" or "late-after-fallback" or
                        "executor-busy" or "preempted-by-gate" or "executor-tools-mismatch") continue;
                    var executor = root.TryGetProperty("executor_id", out var id) ? id.GetString() ?? "" : "";
                    var observed = root.TryGetProperty("observed_at", out var at) && at.TryGetDateTimeOffset(out var endAt)
                        ? endAt : DateTimeOffset.MinValue;
                    DateTimeOffset? started = null;
                    if (root.TryGetProperty("attempt", out var details) && details.ValueKind == JsonValueKind.Object &&
                        details.TryGetProperty("steps", out var intervalSteps) && intervalSteps.ValueKind == JsonValueKind.Array)
                        foreach (var step in intervalSteps.EnumerateArray())
                            if (step.TryGetProperty("started_at", out var start) && start.TryGetDateTimeOffset(out var startAt) &&
                                (started is null || startAt < started)) started = startAt;
                    // Every real attempt occupies an executor, even without a usable duration or binding.
                    var rowIndex = rows.Count;
                    rows.Add(new(observed, executor, "", "", outcome, null, null, started));
                    if (outcome != "accepted") continue;
                    var lane = root.GetProperty("lane").GetString();
                    var filter = root.GetProperty("expected").GetProperty("filter").GetString();
                    if (string.IsNullOrWhiteSpace(lane) || string.IsNullOrWhiteSpace(filter)) continue;
                    var attempt = root.GetProperty("attempt");
                    double? seconds = null;
                    if (attempt.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array &&
                        attempt.TryGetProperty("fetches", out var fetches) && fetches.ValueKind == JsonValueKind.Array)
                    {
                        var starts = steps.EnumerateArray().Select(step => step.GetProperty("started_at").GetDateTimeOffset()).ToArray();
                        var ends = fetches.EnumerateArray().Select(step => step.GetProperty("ended_at").GetDateTimeOffset()).ToArray();
                        if (starts.Length > 0 && ends.Length > 0) seconds = (ends.Max() - starts.Min()).TotalSeconds;
                    }
                    if (seconds is not > 0 && attempt.TryGetProperty("last_status", out var status) &&
                        status.ValueKind == JsonValueKind.Object && status.TryGetProperty("seconds", out var duration) &&
                        duration.TryGetDouble(out var fallback)) seconds = fallback;
                    if (seconds is not { } total || !double.IsFinite(total) || total <= 0) continue;
                    var key = JsonSerializer.Serialize(new[] { lane, filter });
                    keys[key] = (lane, filter);
                    rows[rowIndex] = new(observed, executor, "", key, "accepted", null, total, started);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var concurrent = FindConcurrentRows(rows);
        var accepted = rows.Where(row => row.Outcome == "accepted" && row.RemoteSeconds.HasValue).ToArray();
        var exact = accepted.GroupBy(row => row.Lane, StringComparer.Ordinal)
            .ToDictionary(group => keys[group.Key], Summarize);
        var byLaneName = accepted.GroupBy(row => keys[row.Lane].Lane, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, Summarize, StringComparer.Ordinal);
        return (exact, new(byLaneName));

        RemoteExecutorLaneSeconds Summarize(IGrouping<string, RemoteExecutorOutcomeRow> group)
        {
            var samples = group.ToArray();
            // LastSeconds historically uses the first sample in ledger order.
            return new(group.Key, samples.Length,
                Median(samples.Select(row => row.RemoteSeconds!.Value), 1)!.Value, samples[0].RemoteSeconds!.Value,
                Median(samples.Where(row => HasInterval(row) && !concurrent.Contains(row)).Select(row => row.RemoteSeconds!.Value)),
                Median(samples.Where(concurrent.Contains).Select(row => row.RemoteSeconds!.Value)));
        }
    }

    private static HashSet<RemoteExecutorOutcomeRow> FindConcurrentRows(IEnumerable<RemoteExecutorOutcomeRow> rows)
    {
        var concurrent = new HashSet<RemoteExecutorOutcomeRow>(ReferenceEqualityComparer.Instance);
        foreach (var executor in rows.Where(HasInterval).GroupBy(row => row.ExecutorId, StringComparer.Ordinal))
        {
            var timed = executor.OrderBy(row => row.StartedAt).ToArray();
            var latestEarlierEnd = DateTimeOffset.MinValue;
            for (var index = 0; index < timed.Length; index++)
            {
                var row = timed[index];
                // A prior interval overlaps if it still runs at this start. A later one overlaps
                // if the very next start precedes this end.
                if (row.StartedAt < latestEarlierEnd ||
                    (index + 1 < timed.Length && timed[index + 1].StartedAt < row.ObservedAt))
                    concurrent.Add(row);
                if (row.ObservedAt > latestEarlierEnd) latestEarlierEnd = row.ObservedAt;
            }
        }
        return concurrent;
    }

    private static bool HasInterval(RemoteExecutorOutcomeRow row) =>
        !string.IsNullOrWhiteSpace(row.ExecutorId) && row.StartedAt is { } start && start < row.ObservedAt;

    private static double? Median(IEnumerable<double> values, int minimum = RemoteLaneOfferPolicy.MinimumSamples)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length < minimum) return null;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : sorted[middle - 1] + (sorted[middle] - sorted[middle - 1]) / 2;
    }
}
