// Parallel-safe: every fixture owns its repository, receipts and detached checkouts.
public sealed class ImpactReplayBalancedPoolTests
{
    [Fact]
    public void ExecutedClasses_RetainLaneProvenanceAndSkippedResultDurations()
    {
        using var fixture = new ImpactReplayFixture(balancedPool: true);
        fixture.AddAttempt("pool", failingPoolBeta: true, repeatPool: true);

        var (report, _) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        var classes = Assert.IsAssignableFrom<IReadOnlyList<Mcg.AgentOrchestrator.App.Orchestration.ImpactReplayBalancedClass>>(attempt.BalancedClasses);
        Assert.Equal(3, classes.Count);
        var selected = Assert.Single(classes, c => c.ClassName == "SampleTests.PoolAlphaTests");
        Assert.Equal("Balanced 1", selected.Lane);
        Assert.True(selected.Scanned);
        Assert.True(selected.WouldRun);
        Assert.Equal(2d, selected.Seconds);
        var skipped = classes.Where(c => c.ClassName == "SampleTests.PoolBetaTests").ToArray();
        Assert.Equal(2, skipped.Length);
        Assert.All(skipped, c => { Assert.Equal("Balanced 2", c.Lane); Assert.False(c.WouldRun); });
        Assert.Equal(2, skipped.Select(c => c.TrxPath).Distinct().Count());
        Assert.Contains(skipped, c => c.TrxPath.EndsWith("-run-1.trx", StringComparison.Ordinal));
        Assert.Equal(11d, attempt.PoolSecondsSaved);
        Assert.Equal(16d, attempt.ObservedInfrastructureSeconds);
        Assert.True(attempt.DurationCoverageComplete);
        Assert.Equal(23.5, attempt.EstimatedSerialSecondsSaved);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
    }

    [Fact]
    public void MissingDurations_ExposeIncompleteCoverageWithoutInventingSavings()
    {
        using var fixture = new ImpactReplayFixture(balancedPool: true);
        fixture.AddAttempt("missing-duration", poolDurationMissing: true);
        var (report, _) = fixture.Run();
        var attempt = Assert.Single(report.Attempts);
        Assert.False(attempt.DurationCoverageComplete);
        Assert.All(attempt.BalancedClasses!, c => Assert.Null(c.Seconds));
        Assert.Equal(0d, attempt.PoolSecondsSaved);
        Assert.Equal(1, report.Measurement!.IncompleteDurationCoverageCount);
        Assert.True(attempt.MissCheckComplete);
    }

    [Fact]
    public void WideSelection_SkipsUnselectedClassesAndKeepsObservedCost()
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: 7, balancedPool: true);
        fixture.AddAttempt("abandoned");
        var (report, _) = fixture.Run();
        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.True(Assert.Single(attempt.BalancedClasses!, c => c.ClassName == "SampleTests.PoolAlphaTests").WouldRun);
        Assert.False(Assert.Single(attempt.BalancedClasses!, c => c.ClassName == "SampleTests.PoolBetaTests").WouldRun);
        Assert.Equal(8d, attempt.PoolSecondsSaved);
        Assert.Equal(1d, attempt.NamedLaneObservedSecondsSaved);
        Assert.Equal(13d, attempt.ObservedInfrastructureSeconds);
        Assert.Equal(1, report.Measurement!.ChangedSourceFileCountDistribution[7]);
    }
}
