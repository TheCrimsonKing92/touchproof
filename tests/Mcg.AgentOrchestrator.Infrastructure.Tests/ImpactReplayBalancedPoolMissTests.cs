// Parallel-safe: every fixture owns its repository, receipts and detached checkouts.
public sealed class ImpactReplayBalancedPoolMissTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PoolFailure_ResolvesFromItsReceiptAndChecksSelection(bool selected)
    {
        using var fixture = new ImpactReplayFixture(balancedPool: true,
            changedTestFiles: selected ? new Dictionary<string, string>
            {
                ["PoolBetaTests.cs"] = "namespace SampleTests; public class PoolBetaTests { [Xunit.Fact] public void Changed() { } }"
            } : null);
        fixture.AddAttempt("pool-failure", failingPoolBeta: true);
        var (report, _) = fixture.Run();
        var attempt = Assert.Single(report.Attempts);
        var failure = Assert.Single(attempt.FailingClasses);
        Assert.Equal("SampleTests.PoolBetaTests", failure.ClassName);
        Assert.Equal("Balanced 2", failure.Lane);
        Assert.Equal(selected, failure.WouldRun);
        Assert.Equal(!selected, failure.IsMiss);
        Assert.Equal(selected, attempt.MissCheckComplete);
        Assert.Empty(attempt.UnresolvedFailures!);
        if (selected) Assert.Empty(attempt.EvidenceWarnings);
        else Assert.Contains(attempt.EvidenceWarnings, warning =>
            warning.Contains("SampleTests.PoolBetaTests.Works", StringComparison.Ordinal) &&
            warning.Contains("Recover authoritative exact-identity coverage", StringComparison.Ordinal));
        Assert.Equal(selected ? 0 : 1, report.Measurement!.MissCount);
    }

    [Fact]
    public void RecordedPoolFlake_KeepsClassificationAndExcludesMiss()
    {
        using var fixture = new ImpactReplayFixture(balancedPool: true);
        fixture.AddAttempt("pool-flake", failingPoolBeta: true);
        fixture.AddFlake("pool-flake", "Infrastructure tests: Balanced 2", "PoolBetaTests");
        var (report, markdown) = fixture.Run();
        var attempt = Assert.Single(report.Attempts);
        var failure = Assert.Single(attempt.FailingClasses);
        Assert.True(failure.IsFlakyFailureInSkippedLane);
        Assert.False(failure.IsMiss);
        Assert.Equal(0, report.Measurement!.MissCount);
        Assert.Equal(1, report.Measurement.FlakeExcludedCount);
        Assert.Contains("## Flaky failures in skipped lanes", markdown);
        Assert.Contains("SampleTests.PoolBetaTests", markdown);
        Assert.True(attempt.MissCheckComplete);
    }

    [Fact]
    public void FailureWithoutLaneReceipt_ReportsClassAndOriginatingTrx()
    {
        using var fixture = new ImpactReplayFixture(balancedPool: true);
        fixture.AddAttempt("unresolved", failingPoolBeta: true);
        var directory = Path.GetFullPath(Path.Combine(fixture.OutputDirectory, "..", "acceptance-gate-attempts", "fixture-goal"));
        var original = "unresolved.infrastructure-tests-balanced-2.trx";
        var unknown = "unresolved.unknown-lane.trx";
        File.Move(Path.Combine(directory, original), Path.Combine(directory, unknown));
        var metadata = Path.Combine(directory, "unresolved.attempt.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(metadata))!;
        var paths = json["testResultPaths"]!.AsArray();
        for (var i = 0; i < paths.Count; i++)
            if (paths[i]!.GetValue<string>() == original) paths[i] = unknown;
        File.WriteAllText(metadata, json.ToJsonString());

        var (report, markdown) = fixture.Run();
        var attempt = Assert.Single(report.Attempts);
        Assert.False(attempt.MissCheckComplete);
        Assert.Null(Assert.Single(attempt.FailingClasses).Lane);
        var unresolved = Assert.Single(attempt.UnresolvedFailures!);
        Assert.Equal("SampleTests.PoolBetaTests", unresolved.Identity);
        Assert.Equal(Path.Combine(directory, unknown), unresolved.TrxPath);
        Assert.Contains("## Unresolved failing classes", markdown);
        Assert.Contains(unresolved.TrxPath, markdown);
    }
}
