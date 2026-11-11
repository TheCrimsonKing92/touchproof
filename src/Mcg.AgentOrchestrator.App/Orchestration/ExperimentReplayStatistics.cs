using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExperimentReplayStatistics
{
    internal static ExperimentReplayInterval Bootstrap(IReadOnlyList<double> differences, int seed, string unit)
    {
        if (differences.Count == 0 || differences.Any(v => !double.IsFinite(v)))
            throw new InvalidDataException("Bootstrap requires finite, nonempty observed units.");
        var random = new Random(seed);
        var means = new double[10000];
        for (var sample = 0; sample < means.Length; sample++)
        {
            double sum = 0;
            for (var i = 0; i < differences.Count; i++) sum += differences[random.Next(differences.Count)];
            means[sample] = sum / differences.Count;
        }
        Array.Sort(means);
        return new(differences.Average(), means[249], means[9749], differences.Count, unit);
    }

    internal static ExperimentReplayCalibration Calibrate(IReadOnlyList<ExperimentReplayExecution> executions, int seed)
    {
        var measured = executions.Where(e => e.Slot.Stage == "AA" && !e.Slot.Warmup).ToArray();
        var warmups = executions.Where(e => e.Slot.Stage == "AA" && e.Slot.Warmup).ToArray();
        var pairs = measured.GroupBy(e => e.Slot.Pair).OrderBy(g => g.Key).ToArray();
        if (pairs.Length != 10 || measured.Select(e => e.Slot.CaseId).Distinct().Count() < 3 ||
            measured.Select(e => e.Slot.CaseId).Distinct().Any(id => new[] { "A", "B" }.Any(arm =>
                warmups.Count(e => e.Slot.CaseId == id && e.Slot.Arm == arm && Valid(e)) != 2)) ||
            pairs.Any(p => p.Count() != 2 || !p.Select(e => e.Slot.Arm).Order().SequenceEqual(new[] { "A", "B" }) ||
                p.Any(e => !Valid(e)) || p.Select(e => e.Slot.CaseId).Distinct().Count() != 1 ||
                p.Select(e => e.HostIdentity).Distinct().Count() != 1 || p.Select(e => e.RuntimeIdentity).Distinct().Count() != 1 ||
                p.Select(e => e.ToolchainIdentity).Distinct().Count() != 1 ||
                p.Select(e => string.Join("\n", e.Selection!.Classes)).Distinct().Count() != 1) ||
            measured.Concat(warmups).Select(e => e.PowerThrottling).Distinct().Count() != 1)
            return new("inconclusive", "Ten valid identical-procedure pairs across at least three cases required.", null, null, null, []);
        var differences = pairs.Select(p => p.Single(e => e.Slot.Arm == "B").Resources!.CpuMilliseconds -
            (double)p.Single(e => e.Slot.Arm == "A").Resources!.CpuMilliseconds).ToArray();
        var mean = measured.Average(e => (double)e.Resources!.CpuMilliseconds);
        if (mean <= 0) return new("inconclusive", "Nonpositive A/A CPU denominator.", null, mean, null, differences);
        var interval = Bootstrap(differences, seed, "AA pair (pilot scope)");
        var bias = differences.Average() / mean * 100;
        var passed = Math.Abs(bias) <= 5 && interval.Lower >= -mean * .1 && interval.Upper <= mean * .1;
        return new(passed ? "passed" : "inconclusive", passed ? "Registered calibration thresholds satisfied."
            : "Calibration exceeds registered noise thresholds; operator review required.", bias, mean, interval, differences);
    }

    internal static bool Valid(ExperimentReplayExecution e) => e.State is "passed" or "test-failed" &&
        e.Resources is { CpuMilliseconds: >= 0, PeakMemoryBytes: > 0 } &&
        // Preserve the strict legacy verdict for old receipts; absent new evidence grants no new validity.
        (e.Resources.AccountingEvidence is null ? e.Resources.LaunchAttributionComplete :
            e.Resources.AggregateAccountingValid && e.Resources.AllChildPrioritiesVerified) &&
        e.Resources.AccountingSource.StartsWith("job-object/", StringComparison.Ordinal) &&
        e.Selection is { Classes.Count: > 0 } && e.Priority == "BelowNormal";
}
