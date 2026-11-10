using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExperimentReplayReportWriter
{
    internal sealed record CaseResult(string CaseId, string State, string? Reason, double? A, double? B, double? Difference,
        string CatchCategory, IReadOnlyList<string> ConfirmedDefects, IReadOnlyList<string> ACatches, IReadOnlyList<string> BCatches);
    internal sealed record Report(int ContractVersion, ExperimentReplayRegistration Registration, ExperimentReplayRun Run,
        string Decision, int RegisteredCases, int ValidCases, int ConfirmedCatchDenominator,
        int ACaught, int BCaught, double AnalyticBaselineCpuMilliseconds,
        ExperimentReplayCalibration Calibration, ExperimentReplayInterval? BMinusA,
        IReadOnlyList<CaseResult> Cases, IReadOnlyList<ExperimentReplayExecution> Executions, IReadOnlyList<ExperimentReplayFault> Injected);

    internal static Report Create(ExperimentReplayRegistration registration, ExperimentReplayRun run,
        IReadOnlyList<ExperimentReplayExecution> executions, IReadOnlyList<ExperimentReplayFault>? injected = null)
    {
        var calibration = ExperimentReplayStatistics.Calibrate(executions, registration.Seed);
        var calibrationPolicy = executions.FirstOrDefault(e => e.Slot.Stage == "AA" && !e.Slot.Warmup)?.PowerThrottling;
        var rows = registration.Cases.Select(item =>
        {
            var measured = executions.Where(e => e.Slot.CaseId == item.Id && e.Slot.Stage == "AB" && !e.Slot.Warmup).ToArray();
            var warmups = executions.Where(e => e.Slot.CaseId == item.Id && e.Slot.Stage == "AB" && e.Slot.Warmup).ToArray();
            var valid = warmups.Length == 4 && warmups.All(ExperimentReplayStatistics.Valid) &&
                new[] { "A", "B" }.All(arm => warmups.Count(e => e.Slot.Arm == arm) == 2 &&
                    measured.Concat(warmups).Where(e => e.Slot.Arm == arm).Select(e =>
                        string.Join("\n", e.Selection?.Classes ?? [])).Distinct().Count() == 1) &&
                measured.Length == 6 && Enumerable.Range(0, 3).All(p =>
            {
                var pair = measured.Where(e => e.Slot.Pair == p).ToArray();
                return pair.Length == 2 && pair.Select(e => e.Slot.Arm).Order().SequenceEqual(new[] { "A", "B" }) &&
                    pair.All(ExperimentReplayStatistics.Valid) && pair.Select(e => e.HostIdentity).Distinct().Count() == 1 &&
                    pair.All(e => e.RuntimeIdentity == registration.RuntimeIdentity && e.ToolchainIdentity == registration.ToolchainIdentity) &&
                    pair.All(e => e.PowerThrottling == calibrationPolicy);
            }) && measured.Concat(warmups).Select(e => e.HostIdentity).Distinct().Count() == 1 &&
                warmups.All(e => e.RuntimeIdentity == registration.RuntimeIdentity && e.ToolchainIdentity == registration.ToolchainIdentity &&
                    e.PowerThrottling == calibrationPolicy);
            double? a = valid ? measured.Where(e => e.Slot.Arm == "A").Average(e => (double)e.Resources!.CpuMilliseconds) : null;
            double? b = valid ? measured.Where(e => e.Slot.Arm == "B").Average(e => (double)e.Resources!.CpuMilliseconds) : null;
            var historical = item.Gates.Where(g => g.Outcome == "test-failed").SelectMany(g => g.FailingTests).Distinct().ToArray();
            var reference = executions.Where(e => e.Slot.CaseId == item.Id && e.Slot.Stage == "reference" &&
                ExperimentReplayStatistics.Valid(e)).ToArray();
            // First failure and passing retries remain visible. A retry disagreement is never a confirmed miss.
            var adjudicationConflict = item.Gates.Any(g => g.Outcome == "passed") && historical.Length > 0;
            var confirmed = !adjudicationConflict && reference.Length == 1
                ? historical.Intersect(reference[0].FailingTests, StringComparer.Ordinal).ToArray() : [];
            var catchesA = confirmed.Where(test => measured.Any(e => e.Slot.Arm == "A" &&
                ExperimentReplayStatistics.Valid(e) && e.State == "test-failed" && e.FailingTests.Contains(test, StringComparer.Ordinal))).ToArray();
            var catchesB = confirmed.Where(test => measured.Any(e => e.Slot.Arm == "B" &&
                ExperimentReplayStatistics.Valid(e) && e.State == "test-failed" && e.FailingTests.Contains(test, StringComparer.Ordinal))).ToArray();
            var category = adjudicationConflict ? "retry-disagreement/flake-unresolved" : confirmed.Length > 0 ? "confirmed-reproduced-test-failure"
                : historical.Length > 0 ? "historical-red-unresolved" : item.Gates.All(g => g.Outcome == "passed") ? "green"
                : "excluded-non-test-or-apparatus";
            var preparation = executions.FirstOrDefault(e => e.Slot.CaseId == item.Id && e.State == "nonreplayable");
            return new CaseResult(item.Id, valid ? "valid" : preparation is null ? "invalid" : "nonreplayable",
                valid ? null : preparation?.Reason ?? "Missing or invalid fixed paired measurements; no replacement.",
                a, b, b - a, category, confirmed, catchesA, catchesB);
        }).ToArray();
        var validRows = rows.Where(r => r.State == "valid").ToArray();
        var difference = calibration.State == "passed" && validRows.Length > 0
            ? ExperimentReplayStatistics.Bootstrap(validRows.Select(r => r.Difference!.Value).ToArray(), registration.Seed, "case mean over 3 repetitions") : null;
        var decision = run.State == "complete" && calibration.State == "passed" && validRows.Length == 20
            ? "descriptive-complete; no winner or deployment claim" : "inconclusive; operator review required";
        return new(1, registration, run, decision, registration.Cases.Count, validRows.Length,
            rows.Sum(r => r.ConfirmedDefects.Count), rows.Sum(r => r.ACatches.Count), rows.Sum(r => r.BCatches.Count),
            0, calibration, difference, rows, executions, injected ?? []);
    }

    internal static void Write(TextWriter output, Report report, bool json)
    {
        if (json) { output.WriteLine(JsonSerializer.Serialize(report, ExperimentReplayStore.Json)); return; }
        output.WriteLine($"Experiment replay {report.Run.Id}: {report.Decision}");
        output.WriteLine($"Calibration: {report.Calibration.State}; {report.Calibration.Reason}");
        output.WriteLine($"Cases: {report.ValidCases}/{report.RegisteredCases}; analytic no-test CPU: 0 ms.");
        if (report.BMinusA is { } interval)
            output.WriteLine($"B-A CPU ms: {interval.Estimate:F2}; 95% case-bootstrap [{interval.Lower:F2}, {interval.Upper:F2}].");
        foreach (var item in report.Cases)
            output.WriteLine($"{item.CaseId[..Math.Min(12, item.CaseId.Length)]}: {item.State}; A={item.A?.ToString("F2") ?? "unavailable"} ms; B={item.B?.ToString("F2") ?? "unavailable"} ms; {item.CatchCategory}");
        output.WriteLine($"Historical confirmed defects: {report.ConfirmedCatchDenominator}; A catches: {report.ACaught}; B catches: {report.BCaught}. Injected outcomes are reported separately.");
    }
}
