using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateStatsWaitsReport
{
    internal static readonly string[] LocalStages = ["ready-to-admitted", "admitted-to-execution",
        "scheduler-resource-keys", "scheduler-slot", "shard-permit"];
    internal static readonly string[] RemoteStages = ["remote-claim", "remote-submit", "remote-queue", "remote-runner",
        "remote-worktree", "remote-build-wait", "remote-build", "remote-test", "remote-collect"];
    internal sealed record Stage(string StageName, int Applicable, int Samples, int Missing, int Inapplicable, double? TotalSeconds);
    internal sealed record Row(string Attempt, string Occurrence, string Lane, string Route, int Retry,
        double? SummedLaneWaitSeconds, IReadOnlyList<Stage> Stages, IReadOnlyList<AcceptanceLaneWaitMeasurement> Observations);
    internal sealed record Wall(string Attempt, double? GateWallSeconds);
    internal sealed record Report(IReadOnlyList<Row> Rows, int Skipped, int Occurrences,
        int LaneWaitSamples, int LaneWaitMissing, double? SummedLaneWaitSeconds,
        IReadOnlyList<Stage> Stages, IReadOnlyList<Wall> GateWall);

    internal static Report Read(string directory, int last, int top)
    {
        var attempts = GateStatsWaitsReader.Read(directory, last, out var skipped);
        var occurrences = attempts.SelectMany(a => a.Occurrences).ToArray();
        var rows = occurrences.Select(o => new Row(o.AttemptId, o.OccurrenceId, o.Lane, o.Route, o.Retry,
            Combined(o), Stages([o]), LocalStages.Concat(RemoteStages).Select(stage => Sample(o, stage))
                .OfType<AcceptanceLaneWaitMeasurement>().ToArray())).ToArray();
        var known = rows.Where(row => row.SummedLaneWaitSeconds is not null).ToArray();
        return new(rows.OrderByDescending(row => row.SummedLaneWaitSeconds.HasValue)
            .ThenByDescending(row => row.SummedLaneWaitSeconds).ThenBy(row => row.Attempt, StringComparer.Ordinal)
            .ThenBy(row => row.Lane, StringComparer.Ordinal).ThenBy(row => row.Route, StringComparer.Ordinal)
            .ThenBy(row => row.Retry).ThenBy(row => row.Occurrence, StringComparer.Ordinal).Take(top).ToArray(),
            skipped, rows.Length, known.Length, rows.Length - known.Length,
            known.Length == 0 ? null : Finite(known.Sum(row => row.SummedLaneWaitSeconds!.Value)),
            Stages(occurrences), attempts.Select(a => new Wall(a.Id, a.GateWallSeconds)).ToArray());
    }

    private static Stage[] Stages(IReadOnlyList<AcceptanceLaneWaitOccurrence> occurrences) =>
        LocalStages.Concat(RemoteStages).Select(stage =>
        {
            var applicable = occurrences.Where(o => Applies(o, stage)).ToArray();
            var samples = applicable.Select(o => Sample(o, stage)).OfType<AcceptanceLaneWaitMeasurement>().ToArray();
            return new Stage(stage, applicable.Length, samples.Length, applicable.Length - samples.Length,
                occurrences.Count - applicable.Length, samples.Length == 0 ? null : Finite(samples.Sum(m => m.Seconds!.Value)));
        }).ToArray();

    private static bool Applies(AcceptanceLaneWaitOccurrence o, string stage) => o.Route == "unknown" ||
        stage is "ready-to-admitted" or "admitted-to-execution" ||
        (o.Route == "remote" && RemoteStages.Contains(stage, StringComparer.Ordinal)) ||
        o.ApplicableStages.Contains(stage, StringComparer.Ordinal);

    private static AcceptanceLaneWaitMeasurement? Sample(AcceptanceLaneWaitOccurrence o, string stage)
    {
        var values = o.Measurements.Where(m => m.Stage == stage).ToArray();
        return values.Length == 1 && Valid(o, values[0]) ? values[0] : null;
    }

    private static bool Valid(AcceptanceLaneWaitOccurrence o, AcceptanceLaneWaitMeasurement m)
    {
        if (!Applies(o, m.Stage) || m.Seconds is not { } seconds || !double.IsFinite(seconds) || seconds < 0 ||
            string.IsNullOrWhiteSpace(m.Boundary) || string.IsNullOrWhiteSpace(m.Source)) return false;
        if (m.Source != "local-monotonic")
            return m.Boundary == "existing-remote-phase" && o.Route == "remote" && (m.Stage switch
            {
                "remote-submit" => m.Source == "submission-steps-local-utc",
                "remote-test" => m.Source == "remote-trx-clock",
                "remote-runner" or "remote-worktree" or "remote-build-wait" or "remote-build" => m.Source == "remote-status-duration",
                _ => false
            });
        if (string.IsNullOrWhiteSpace(m.ClockDomain) || m.StartSeconds is not { } start ||
            m.EndSeconds is not { } end || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start ||
            Math.Abs((end - start) - seconds) > 0.0000001) return false;
        return m.Stage switch
        {
            "ready-to-admitted" => m.Boundary == "all-local-grants",
            "admitted-to-execution" => m.Boundary == (o.Route == "remote" ? "submission-start" : "executor-invoked"),
            "scheduler-resource-keys" => o.Route == "local" && m.Boundary is "scheduler-and-keys-granted" or "cancelled" or "faulted",
            "scheduler-slot" => o.Route == "local" && m.Boundary is "slot-acquired" or "cancelled" or "scheduler-ended" or "faulted",
            "shard-permit" => o.Route == "local" && m.Boundary is "permit-acquired" or "cancelled" or "acquisition-ended" or "faulted",
            "remote-claim" => o.Route == "remote" && m.Boundary is "claim-returned" or "cancelled",
            _ => false
        };
    }

    // Stage totals are attribution, not additive elapsed time. Only intervals in one clock
    // domain may contribute to this union; remote build-wait is never folded into it.
    private static double? Combined(AcceptanceLaneWaitOccurrence o)
    {
        var local = LocalStages.Concat(["remote-claim"]).Where(stage => Applies(o, stage)).ToArray();
        var measurements = local.Select(stage => Sample(o, stage)).ToArray();
        if (measurements.Length == 0 || measurements.Any(m => m is null) ||
            measurements.Select(m => m!.ClockDomain).Distinct(StringComparer.Ordinal).Count() != 1) return null;
        var intervals = measurements.Select(m => (Start: m!.StartSeconds!.Value, End: m.EndSeconds!.Value))
            .OrderBy(m => m.Start).ToArray();
        double total = 0, start = intervals[0].Start, end = intervals[0].End;
        foreach (var interval in intervals.Skip(1))
        {
            if (interval.Start <= end) end = Math.Max(end, interval.End);
            else { total += end - start; start = interval.Start; end = interval.End; }
        }
        return Finite(total + end - start);
    }
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
