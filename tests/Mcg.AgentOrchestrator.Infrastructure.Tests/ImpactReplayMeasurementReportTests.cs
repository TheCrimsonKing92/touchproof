using System.Text.Json;

// Parallel-safe: fixture repository, receipts and report files are private to each test.
public sealed class ImpactReplayMeasurementReportTests
{
    [Fact]
    public void WrittenReport_ContainsDurationWeightedSummaryWithoutRemovedLimit()
    {
        using var fixture = new ImpactReplayFixture(balancedPool: true);
        fixture.AddAttempt("summary", failingPoolBeta: true);
        var (report, markdown) = fixture.Run();
        var summary = Assert.IsType<Mcg.AgentOrchestrator.App.Orchestration.ImpactReplayMeasurement>(report.Measurement);
        Assert.Equal(1, summary.ChangedSourceFileCountDistribution[1]);
        Assert.Equal(23.5, summary.NamedLaneSecondsSaved);
        Assert.Equal(8d, summary.BalancedPoolSecondsSaved);
        Assert.Equal(13d, summary.ObservedInfrastructureSeconds);
        Assert.Equal(9d / 13, summary.ShareOfTestSecondsSkipped);
        Assert.Equal(1, summary.MissCount);
        Assert.Equal(0, summary.FlakeExcludedCount);
        // The skipped failure remains a miss, but this fixture has no authoritative history coverage.
        Assert.Equal(0, summary.CompleteMissCheckCount);
        Assert.Equal(0, summary.IncompleteDurationCoverageCount);
        Assert.Contains("## Balanced pool and skip measurement", markdown);
        Assert.DoesNotContain("Maximum changed source files", markdown);
        Assert.DoesNotContain("max-changed-source-files", markdown);
        Assert.Contains("Changed source file count distribution: 1: 1", markdown);
        Assert.Contains("Named-lane estimated seconds saved: 23.5", markdown);
        Assert.Contains("Balanced-pool seconds saved: 8", markdown);
        Assert.Contains("Share of Infrastructure test seconds skipped (readable TRX durations): 69.2", markdown);
        Assert.Contains("Miss count (true misses): 1", markdown);
        Assert.Contains("Flake-excluded count (known flaky skipped failures): 0", markdown);
        Assert.Contains("Complete miss checks: 0", markdown);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.OutputDirectory, "impact-replay.json")));
        Assert.Equal(8d, json.RootElement.GetProperty("measurement").GetProperty("balancedPoolSecondsSaved").GetDouble());
        var measurement = json.RootElement.GetProperty("measurement");
        Assert.False(measurement.TryGetProperty("maximumChangedSourceFiles", out _));
        Assert.Equal(0, measurement.GetProperty("completeMissCheckCount").GetInt32());
        Assert.Equal(summary.MissCount, measurement.GetProperty("missCount").GetInt32());
        Assert.Equal(summary.FlakeExcludedCount, measurement.GetProperty("flakeExcludedCount").GetInt32());
        var interpretation = measurement.GetProperty("interpretation").GetString()!;
        Assert.Contains(interpretation, markdown);
        Assert.Contains("Historical summaries may include skipped flaky failures", interpretation);
        Assert.Contains("not directly comparable", interpretation);
        Assert.Contains("Zero true misses does not establish selection safety", interpretation);
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
