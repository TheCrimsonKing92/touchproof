using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;
using static ExperimentReplayTestData;

// Parallel-safe: deterministic calculations over private immutable values.
public sealed class ExperimentReplayStatisticsTests
{
    [Fact]
    public void Bootstrap_ConstantPairedDifferences_HasExactInterval()
    {
        var result = ExperimentReplayStatistics.Bootstrap([20, 20, 20], 42, "case");
        Assert.Equal(20, result.Estimate);
        Assert.Equal(20, result.Lower);
        Assert.Equal(20, result.Upper);
        Assert.Equal(3, result.Units);
    }

    [Fact]
    public void Calibration_IdenticalCosts_UsesTenPairsAndPasses()
    {
        var values = ExperimentReplaySchedule.Create(Registration()).Where(s => s.Stage == "AA").Select(s => Execution(s)).ToArray();
        var result = ExperimentReplayStatistics.Calibrate(values, 42);
        Assert.Equal("passed", result.State);
        Assert.Equal(10, result.Difference!.Units);
        Assert.Equal(10, result.PairDifferences.Count);
        Assert.Equal(0, result.SignedBiasPercent);
    }

    [Fact]
    public void Calibration_SignedBiasOrMissingAccounting_StopsInterpretation()
    {
        var slots = ExperimentReplaySchedule.Create(Registration()).Where(s => s.Stage == "AA").ToArray();
        var biased = slots.Select(s => Execution(s, s.Arm == "A" ? 100 : 120)).ToArray();
        Assert.Equal("inconclusive", ExperimentReplayStatistics.Calibrate(biased, 42).State);
        var absent = slots.Select(s => Execution(s) with { Resources = null }).ToArray();
        Assert.Equal("inconclusive", ExperimentReplayStatistics.Calibrate(absent, 42).State);
    }

    [Fact]
    public void Report_CaseMeans_ReportsBMinusAAndInvalidDenominator()
    {
        var registration = Registration();
        var run = new Mcg.AgentOrchestrator.Infrastructure.ExperimentReplayRun("run", registration.Id, "complete", null, Now, Now);
        var values = ExperimentReplaySchedule.Create(registration).Select(s => Execution(s, s.Stage == "AA" || s.Arm == "A" ? 100 : 120)).ToArray();
        var report = ExperimentReplayReportWriter.Create(registration, run, values);
        Assert.Equal(20, report.ValidCases);
        Assert.Equal(20, report.BMinusA!.Estimate);
        Assert.Equal(20, report.BMinusA.Units);
        Assert.Equal(0, report.AnalyticBaselineCpuMilliseconds);
        var incomplete = ExperimentReplayReportWriter.Create(registration, run, values.SkipLast(1).ToArray());
        Assert.Equal(19, incomplete.ValidCases);
        Assert.StartsWith("inconclusive", incomplete.Decision);
        Assert.Equal(20, incomplete.Cases.Count);
    }

    [Fact]
    public void CatchScoring_UnrelatedFailureAndPassingRetry_AreExcluded()
    {
        var registration = Registration();
        var red = new Mcg.AgentOrchestrator.Infrastructure.ExperimentReplayGate("red", Now, "test-failed", ["Exact.Defect"], []);
        registration = registration with { Cases = registration.Cases.Select((c, i) => i == 0 ? c with { Gates = [red] } : c).ToArray() };
        var run = new Mcg.AgentOrchestrator.Infrastructure.ExperimentReplayRun("run", registration.Id, "complete", null, Now, Now);
        var measured = ExperimentReplaySchedule.Create(registration).Select(s => Execution(s) with
        {
            State = s.CaseId == "case-000" && s.Stage == "AB" ? "test-failed" : "passed",
            FailingTests = s.CaseId == "case-000" && s.Stage == "AB" ? [s.Arm == "A" ? "Unrelated.Defect" : "Exact.Defect"] : []
        }).ToList();
        measured.Add(Execution(new(999, "case-000", "reference", 0, "B", false), state: "test-failed") with { FailingTests = ["Exact.Defect"] });
        var report = ExperimentReplayReportWriter.Create(registration, run, measured);
        Assert.Equal(1, report.ConfirmedCatchDenominator);
        Assert.Equal(0, report.ACaught);
        Assert.Equal(1, report.BCaught);
        var retried = registration with { Cases = registration.Cases.Select((c, i) => i == 0
            ? c with { Gates = [red, red with { AttemptId = "retry", Outcome = "passed", FailingTests = [] }] } : c).ToArray() };
        var unresolved = ExperimentReplayReportWriter.Create(retried, run, measured);
        Assert.Equal(0, unresolved.ConfirmedCatchDenominator);
        Assert.Equal("retry-disagreement/flake-unresolved", unresolved.Cases[0].CatchCategory);
    }
}
