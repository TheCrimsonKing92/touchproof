namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateStatsReport
{
    internal sealed record LaneRow(string Lane, int Gates, double MedianSeconds, double P90Seconds, double MaxSeconds);
    internal sealed record ClassRow(string Class, int Gates, double TotalSeconds, double MedianSeconds);
    internal sealed record CompareRow(string Lane, int BeforeGates, double? BeforeMedian, double? BeforeP90,
        int AfterGates, double? AfterMedian, double? AfterP90, double? MedianDelta, double? P90Delta);
    internal sealed record Report<T>(IReadOnlyList<T> Rows, int Skipped);

    internal static Report<LaneRow> Lanes(string directory, int last)
    {
        var observations = GateStatsReader.ReadLaneObservations(directory, out var skipped);
        var rows = observations.GroupBy(row => row.LaneName, StringComparer.Ordinal).Select(group =>
        {
            var values = group.OrderByDescending(row => row.RecordedAt).Take(last)
                .Select(row => row.DurationSeconds).Order().ToArray();
            return new LaneRow(group.Key, values.Length, Median(values), P90(values), values[^1]);
        }).OrderBy(row => row.Lane, StringComparer.Ordinal).ToArray();
        return new(rows, skipped);
    }

    internal static Report<ClassRow> Classes(string directory, int last, string? lane, int top)
    {
        var attempts = GateStatsReader.ReadGateAttempts(directory, lane, last, out var skipped);
        var rows = attempts.SelectMany(attempt => attempt.Classes).GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var values = group.Select(pair => pair.Value).Order().ToArray();
                return new ClassRow(group.Key, values.Length, values.Sum(), Median(values));
            }).OrderByDescending(row => row.TotalSeconds).ThenBy(row => row.Class, StringComparer.Ordinal).Take(top).ToArray();
        return new(rows, skipped);
    }

    internal static Report<CompareRow> Compare(string directory, int last, DateTimeOffset before, DateTimeOffset after)
    {
        var observations = GateStatsReader.ReadLaneObservations(directory, out var skipped);
        var rows = observations.GroupBy(row => row.LaneName, StringComparer.Ordinal).Select(group =>
        {
            var earlier = group.Where(row => row.RecordedAt < before).OrderByDescending(row => row.RecordedAt).Take(last)
                .Select(row => row.DurationSeconds).Order().ToArray();
            var later = group.Where(row => row.RecordedAt > after).OrderBy(row => row.RecordedAt).Take(last)
                .Select(row => row.DurationSeconds).Order().ToArray();
            double? bm = earlier.Length == 0 ? null : Median(earlier), bp = earlier.Length == 0 ? null : P90(earlier);
            double? am = later.Length == 0 ? null : Median(later), ap = later.Length == 0 ? null : P90(later);
            return new CompareRow(group.Key, earlier.Length, bm, bp, later.Length, am, ap, am - bm, ap - bp);
        }).Where(row => row.BeforeGates + row.AfterGates > 0).OrderBy(row => row.Lane, StringComparer.Ordinal).ToArray();
        return new(rows, skipped);
    }

    // Inputs are sorted; p90 uses nearest rank, including the maximum for small samples.
    private static double Median(double[] values) => values.Length % 2 == 1 ? values[values.Length / 2]
        : values[values.Length / 2 - 1] / 2 + values[values.Length / 2] / 2;
    private static double P90(double[] values) => values[(int)Math.Ceiling(values.Length * 0.9) - 1];
}
