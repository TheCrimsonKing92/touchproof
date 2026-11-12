using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe integration: fixture owns its Git repository, receipts, and worktree cleanup.
public sealed class ImpactReplayHistorySelectionTests
{
    [Fact]
    public void SinceAndLimit_ApplyBeforeAnyHistoricalQuery()
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("a-selected", failingBeta: true);
        fixture.AddAttempt("z-unselected", failingBeta: true);
        fixture.AddBetaFailure("z-unselected", "UnselectedFailure");
        var queries = new List<string[]>();
        var history = new ImpactReplayHistoryQuery(tests =>
        {
            queries.Add(tests.ToArray());
            return new(new Dictionary<string, GateStatsReport.FlakeRow>(), true, []);
        });
        var empty = fixture.ReadWithHistory(history, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Empty(empty.Attempts);
        Assert.Empty(queries);
        var report = fixture.ReadWithHistory(history, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("a-selected", Assert.Single(report.Attempts).AttemptId);
        Assert.Equal(new[] { "SampleTests.BetaTests.Works" }, Assert.Single(queries));
        Assert.Empty(report.UnreplayableAttempts);
        Assert.Empty(report.CleanupErrors);
    }

    [Fact]
    public void PassingCase_WithUnrelatedRetainedHistory_ParsesNoHistory()
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("passing");
        fixture.Write(".orchestrator/acceptance-gate-attempts/unrelated/goal-20250101000000000-00000000000000000000000000000001.lane.trx",
            "malformed unrelated TRX");
        var (report, _) = fixture.Run();
        var attempt = Assert.Single(report.Attempts);
        Assert.True(attempt.MissCheckComplete);
        Assert.Empty(attempt.FailingClasses);
        Assert.Empty(report.HistoricalEvidence!.QueriedIdentities);
        Assert.Equal(0, report.HistoricalEvidence.MetadataReads);
        Assert.Equal(0, report.HistoricalEvidence.TrxParses);
        Assert.Empty(report.HistoricalEvidence.ParsedPaths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfirmedFailureReceipt_RequiresPositiveRepeatedFailure(bool repeatedFailure)
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("confirmed", failingBeta: true, rerunBetaPass: repeatedFailure);
        if (repeatedFailure) fixture.RewriteBetaRerun("confirmed", [("Works", "Failed")]);
        fixture.AddFlake("confirmed", outcome: AcceptanceLaneRerunEvidence.ConfirmedFailure, transform: row =>
            repeatedFailure ? row with
            {
                FirstPredicate = AcceptanceShardCompletionPredicates.FailingTrx,
                RerunPredicate = AcceptanceShardCompletionPredicates.FailingTrx,
                FirstTestResultPaths = [Path.Combine(Path.GetDirectoryName(fixture.FlakeLedgerPath)!,
                    "acceptance-gate-attempts", "fixture-goal", "confirmed.infrastructure-tests-beta-lane.trx")],
                RerunTestResultPaths = [Path.Combine(Path.GetDirectoryName(fixture.FlakeLedgerPath)!,
                    "acceptance-gate-attempts", "fixture-goal", "confirmed.infrastructure-tests-beta-lane-run-1.trx")],
                RerunFailingTestIdentities = ["SampleTests.BetaTests.Works"]
            } : row with { RerunPredicate = AcceptanceShardCompletionPredicates.TimedOut });
        var calls = 0;
        var query = new ImpactReplayHistoryQuery(_ =>
        {
            calls++;
            return new(new Dictionary<string, GateStatsReport.FlakeRow>(), true, []);
        });
        var report = fixture.ReadWithHistory(query);
        var attempt = Assert.Single(report.Attempts);
        var failure = Assert.Single(attempt.FailingClasses);
        Assert.Equal(repeatedFailure, failure.ConfirmedHardFailure);
        Assert.True(failure.IsMiss);
        Assert.Null(failure.FlakeEvidence);
        Assert.Equal(repeatedFailure, attempt.MissCheckComplete);
        Assert.Equal(repeatedFailure ? 0 : 1, calls);
        if (repeatedFailure) Assert.Empty(attempt.EvidenceWarnings);
        else Assert.Contains(attempt.EvidenceWarnings, value => value.Contains("Selected rerun evidence incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public void ExactPassingRerun_DoesNotNeedHistoricalLookup()
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("rerun", failingBeta: true, rerunBetaPass: true);
        var calls = 0;
        var query = new ImpactReplayHistoryQuery(_ => { calls++; throw new InvalidOperationException("History opened"); });
        var attempt = Assert.Single(fixture.ReadWithHistory(query).Attempts);
        Assert.Equal("rerun", Assert.Single(attempt.FailingClasses).FlakeEvidence);
        Assert.True(attempt.MissCheckComplete);
        Assert.Equal(0, calls);
    }
}
