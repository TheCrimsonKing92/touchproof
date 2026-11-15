using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure evaluation of caller-owned rows and a fixed time.
public sealed class RemoteExecutorAcceptanceStallTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Xunit.Theory]
    [Xunit.InlineData(0L, true, 1)]
    [Xunit.InlineData(-1L, false, 0)]
    public void Offer_AtLowerWindowBoundary_IsIncluded(long ticks, bool stalled, int offers)
    {
        var result = RemoteExecutorAcceptanceStall.Evaluate(
            [Row(Now.AddMinutes(-60).AddTicks(ticks), "unreachable", "boundary offer")], Now);
        Assert.Equal(stalled, result.Stalled);
        Assert.Equal(offers, result.Offers);
        Assert.Null(result.LastAcceptedAt);
        Assert.Equal(stalled ? "boundary offer" : null, result.LastOfferReason);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0L, false, 2)]
    [Xunit.InlineData(-1L, true, 1)]
    public void Acceptance_AtLowerWindowBoundary_SuppressesStall(long ticks, bool stalled, int offers)
    {
        var acceptedAt = Now.AddMinutes(-60).AddTicks(ticks);
        var result = RemoteExecutorAcceptanceStall.Evaluate(
            [Row(acceptedAt, "accepted"), Row(Now, "lease-expired", "latest offer")], Now);
        Assert.Equal(stalled, result.Stalled);
        Assert.Equal(offers, result.Offers);
        Assert.Equal(acceptedAt, result.LastAcceptedAt);
        Assert.Equal("latest offer", result.LastOfferReason);
    }

    [Xunit.Fact]
    public void Offers_UnsortedAndEqualTimestamps_SelectLatestThenLaterLedgerEntry()
    {
        var result = RemoteExecutorAcceptanceStall.Evaluate([
            Row(Now.AddMinutes(-1), "unreachable", "first newest"),
            Row(Now.AddMinutes(-20), "unreachable", "oldest"),
            Row(Now.AddMinutes(-1), "lease-expired", "later newest"),
            Row(Now.AddMinutes(-10), "executor-tools-unverified", "older last entry"),
            Row(Now, "executor-busy", "newest non-attempt")], Now);
        Assert.True(result.Stalled);
        Assert.Equal(4, result.Offers);
        Assert.Equal("later newest", result.LastOfferReason);
        Assert.Null(result.LastAcceptedAt);
    }

    [Xunit.Fact]
    public void Acceptance_UnsortedOlderHistory_ReportsLatestAtOrBeforeNow()
    {
        var result = RemoteExecutorAcceptanceStall.Evaluate([
            Row(Now.AddMinutes(-61), "accepted"), Row(Now.AddMinutes(-90), "accepted"),
            Row(Now.AddTicks(1), "accepted"), Row(Now, "unreachable", "current")], Now);
        Assert.True(result.Stalled);
        Assert.Equal(1, result.Offers);
        Assert.Equal(Now.AddMinutes(-61), result.LastAcceptedAt);
    }

    [Xunit.Fact]
    public void FutureRows_DoNotCreateOffersOrSuppressCurrentStall()
    {
        var future = new[] { Row(Now.AddTicks(1), "accepted"),
            Row(Now.AddMinutes(1), "unreachable", "future offer") };
        var empty = RemoteExecutorAcceptanceStall.Evaluate(future, Now);
        Assert.False(empty.Stalled);
        Assert.Equal(0, empty.Offers);
        Assert.Null(empty.LastAcceptedAt);
        Assert.Null(empty.LastOfferReason);

        var current = RemoteExecutorAcceptanceStall.Evaluate(
            future.Append(Row(Now, "unreachable", "current offer")), Now);
        Assert.True(current.Stalled);
        Assert.Equal(1, current.Offers);
        Assert.Null(current.LastAcceptedAt);
        Assert.Equal("current offer", current.LastOfferReason);
    }

    [Xunit.Fact]
    public void AcceptedAtNow_SuppressesStallAndCountsAsOffer()
    {
        var result = RemoteExecutorAcceptanceStall.Evaluate(
            [Row(Now.AddMinutes(-1), "unreachable"), Row(Now, "accepted", "success")], Now);
        Assert.False(result.Stalled);
        Assert.Equal(2, result.Offers);
        Assert.Equal(Now, result.LastAcceptedAt);
        Assert.Equal("success", result.LastOfferReason);
    }

    [Xunit.Theory]
    [Xunit.InlineData("not-eligible-exclusive-resource", false)]
    [Xunit.InlineData("late-after-fallback", false)]
    [Xunit.InlineData("executor-busy", false)]
    [Xunit.InlineData("preempted-by-gate", false)]
    [Xunit.InlineData("executor-tools-mismatch", false)]
    [Xunit.InlineData("executor-tools-unverified", true)]
    [Xunit.InlineData("Executor-busy", true)]
    public void NonAttemptClassification_PreservesReportMatching(string outcome, bool isOffer)
    {
        var result = RemoteExecutorAcceptanceStall.Evaluate([Row(Now, outcome, "reason")], Now);
        Assert.Equal(isOffer, result.Stalled);
        Assert.Equal(isOffer ? 1 : 0, result.Offers);
        Assert.Equal(isOffer ? "reason" : null, result.LastOfferReason);
    }

    [Xunit.Fact]
    public void LatestOffer_NullReason_DoesNotReuseEarlierReason()
    {
        var result = RemoteExecutorAcceptanceStall.Evaluate(
            [Row(Now.AddMinutes(-1), "unreachable", "old reason"), Row(Now, "lease-expired")], Now);
        Assert.True(result.Stalled);
        Assert.Equal(2, result.Offers);
        Assert.Null(result.LastOfferReason);
    }

    [Xunit.Fact]
    public void WindowBoundary_ComparedAsInstants_IncludesEquivalentOffset()
    {
        var result = RemoteExecutorAcceptanceStall.Evaluate(
            [Row(Now.AddMinutes(-60).ToOffset(TimeSpan.FromHours(-6)), "unreachable")], Now);
        Assert.True(result.Stalled);
        Assert.Equal(1, result.Offers);
    }

    private static RemoteExecutorOutcomeRow Row(DateTimeOffset at, string outcome, string? reason = null) =>
        new(at, "one", "gate", "lane", outcome, reason);
}
