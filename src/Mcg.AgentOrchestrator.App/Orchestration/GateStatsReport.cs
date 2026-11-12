namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateStatsReport
{
    internal static GateStatsWaitsReport.Report Waits(string directory, int last, int top) =>
        GateStatsWaitsReport.Read(directory, last, top);
    internal sealed record LaneRow(string Lane, int Gates, double MedianSeconds, double P90Seconds, double MaxSeconds);
    internal sealed record ClassRow(string Class, int Gates, double TotalSeconds, double MedianSeconds);
    internal sealed record CompareRow(string Lane, int BeforeGates, double? BeforeMedian, double? BeforeP90,
        int AfterGates, double? AfterMedian, double? AfterP90, double? MedianDelta, double? P90Delta);
    internal sealed record Report<T>(IReadOnlyList<T> Rows, int Skipped);
    internal sealed record FlakeRow(string Test, int Attempts, int Pass, int Flake, int Hard, double FlakeRate);
    internal sealed record FlakeReport(string Form, int Gates, int Skipped, int Nonterminal, IReadOnlyList<FlakeRow> Tests);
    // Ordered by precedence when the same test appears in multiple lanes of an attempt.
    private enum TestAttemptOutcome { Nonterminal, CleanPass, Flake, HardFailure }

    internal static FlakeReport Flakes(string directory, int last, int top)
    {
        var attempts = GateStatsReader.ReadGateRuns(directory, last, out var skipped);
        return Flakes(attempts, skipped, top);
    }

    internal static FlakeReport Flakes(IReadOnlyList<GateStatsReader.GateRunAttempt> attempts, int skipped, int top)
    {
        var outcomes = new Dictionary<string, List<TestAttemptOutcome>>(StringComparer.Ordinal);
        var nonterminal = 0;
        foreach (var attempt in attempts)
        {
            var tests = attempt.Runs.SelectMany(run => run.Results
                .Where(result => !string.IsNullOrWhiteSpace(result.Test))
                .Select(result => (Test: result.Test!, run.Lane, run.Run, result.Outcome)))
                .GroupBy(result => result.Test, StringComparer.Ordinal);
            foreach (var test in tests)
            {
                var outcome = test.GroupBy(result => result.Lane, StringComparer.OrdinalIgnoreCase)
                    .Select(lane => ClassifyLane(lane.Select(result => (result.Run, result.Outcome)))).Max();
                if (outcome == TestAttemptOutcome.Nonterminal) { nonterminal++; continue; }
                if (!outcomes.TryGetValue(test.Key, out var values)) outcomes[test.Key] = values = [];
                values.Add(outcome);
            }
        }
        var rows = outcomes.Select(pair =>
        {
            var flake = pair.Value.Count(outcome => outcome == TestAttemptOutcome.Flake);
            return new FlakeRow(pair.Key, pair.Value.Count,
                pair.Value.Count(outcome => outcome == TestAttemptOutcome.CleanPass), flake,
                pair.Value.Count(outcome => outcome == TestAttemptOutcome.HardFailure), (double)flake / pair.Value.Count);
        }).OrderByDescending(row => row.Flake).ThenByDescending(row => row.FlakeRate)
            .ThenBy(row => row.Test, StringComparer.Ordinal).Take(top).ToArray();
        return new("flakes", attempts.Count, skipped, nonterminal, rows);
    }

    private static TestAttemptOutcome ClassifyLane(IEnumerable<(int Run, string? Outcome)> results)
    {
        var runs = results.Where(result => IsOutcome(result.Outcome, "Passed") || IsOutcome(result.Outcome, "Failed"))
            .GroupBy(result => result.Run).OrderBy(group => group.Key)
            .Select(group => group.Any(result => IsOutcome(result.Outcome, "Failed"))).ToArray();
        if (runs.Length == 0) return TestAttemptOutcome.Nonterminal;
        if (runs[^1]) return TestAttemptOutcome.HardFailure;
        return runs.Any(failed => failed) ? TestAttemptOutcome.Flake : TestAttemptOutcome.CleanPass;
    }

    private static bool IsOutcome(string? outcome, string expected) =>
        string.Equals(outcome, expected, StringComparison.OrdinalIgnoreCase);

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
