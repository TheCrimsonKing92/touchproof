using System.Text.Json;

// Parallel-safe: fixture repository, receipts and report files are private to each test.
public sealed class ImpactReplayMeasurementReportTests
{
    [Fact]
    public void WrittenReport_ContainsDurationWeightedSummaryAndConfiguredLimit()
    {
        using var fixture = new ImpactReplayFixture(balancedPool: true);
        fixture.AddAttempt("summary", failingPoolBeta: true);
        var (report, markdown) = fixture.Run(extraArguments: ["--max-changed-source-files", "20"]);
        var summary = Assert.IsType<Mcg.AgentOrchestrator.App.Orchestration.ImpactReplayMeasurement>(report.Measurement);
        Assert.Equal(20, summary.MaximumChangedSourceFiles);
        Assert.Equal(1, summary.ChangedSourceFileCountDistribution[1]);
        Assert.Equal(23.5, summary.NamedLaneSecondsSaved);
        Assert.Equal(8d, summary.BalancedPoolSecondsSaved);
        Assert.Equal(13d, summary.ObservedInfrastructureSeconds);
        Assert.Equal(9d / 13, summary.ShareOfTestSecondsSkipped);
        Assert.Equal(1, summary.MissCount);
        Assert.Equal(1, summary.CompleteMissCheckCount);
        Assert.Equal(0, summary.IncompleteDurationCoverageCount);
        Assert.Contains("Maximum changed source files: 20", markdown);
        Assert.Contains("Changed source file count distribution: 1: 1", markdown);
        Assert.Contains("Named-lane estimated seconds saved: 23.5", markdown);
        Assert.Contains("Balanced-pool seconds saved: 8", markdown);
        Assert.Contains("Share of Infrastructure test seconds skipped (readable TRX durations): 69.2", markdown);
        Assert.Contains("Miss count (including recorded flakes): 1", markdown);
        Assert.Contains("Complete miss checks: 1", markdown);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.OutputDirectory, "impact-replay.json")));
        Assert.Equal(8d, json.RootElement.GetProperty("measurement").GetProperty("balancedPoolSecondsSaved").GetDouble());
        Assert.Equal(2, json.RootElement.GetProperty("attempts")[0].GetProperty("balancedClasses").GetArrayLength());
    }

    [Fact]
    public void MultipleAttempts_AggregateDurationsBeforeDividing()
    {
        using var fixture = new ImpactReplayFixture(balancedPool: true);
        fixture.AddAttempt("short");
        fixture.AddAttempt("repeated", repeatPool: true);
        var summary = fixture.Run().Report.Measurement!;
        Assert.Equal(29d, summary.ObservedInfrastructureSeconds);
        Assert.Equal(19d, summary.BalancedPoolSecondsSaved);
        Assert.Equal(21d / 29, summary.ShareOfTestSecondsSkipped);
        Assert.Equal(2, summary.CompleteMissCheckCount);
        Assert.Equal(2, summary.ChangedSourceFileCountDistribution[1]);
    }
}
