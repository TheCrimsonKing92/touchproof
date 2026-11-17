using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorMaxDurationStopDeferralTests
{
    private static readonly DateTimeOffset DeferralStartedAt =
        new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void DecideWithoutAttemptsStopsImmediately()
    {
        var verdict = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt,
            DeferralStartedAt,
            [],
            TimeSpan.FromMinutes(40));

        Assert.Equal(ConductorMaxDurationStopVerdictKind.Stop, verdict.Kind);
        Assert.Empty(verdict.Attempts);
    }

    [Xunit.Fact]
    public void DecideBeforeCeilingDefersAndOrdersAttempts()
    {
        var later = Attempt("attempt-b", DeferralStartedAt.AddMinutes(-2));
        var earlier = Attempt("attempt-a", DeferralStartedAt.AddMinutes(-3));

        var verdict = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddMinutes(39),
            DeferralStartedAt,
            [later, earlier],
            TimeSpan.FromMinutes(40));

        Assert.Equal(ConductorMaxDurationStopVerdictKind.Defer, verdict.Kind);
        Assert.Equal(["attempt-a", "attempt-b"], verdict.Attempts.Select(attempt => attempt.AttemptId));
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(-1)]
    public void DecideAtNonPositiveCeilingExpires(int ceilingSeconds)
    {
        var verdict = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt,
            DeferralStartedAt,
            [Attempt("attempt-a", DeferralStartedAt.AddMinutes(-1))],
            TimeSpan.FromSeconds(ceilingSeconds));

        Assert.Equal(ConductorMaxDurationStopVerdictKind.StopExpired, verdict.Kind);
        Assert.Equal("attempt-a", Assert.Single(verdict.Attempts).AttemptId);
    }

    [Xunit.Fact]
    public void DecideAtCeilingExpires()
    {
        var verdict = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddMinutes(40),
            DeferralStartedAt,
            [Attempt("attempt-a", DeferralStartedAt.AddMinutes(-1))],
            TimeSpan.FromMinutes(40));

        Assert.Equal(ConductorMaxDurationStopVerdictKind.StopExpired, verdict.Kind);
    }

    [Xunit.Fact]
    public void DecideAfterClockMovesBackwardStillDefers()
    {
        var verdict = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddMinutes(-1),
            DeferralStartedAt,
            [Attempt("attempt-a", DeferralStartedAt.AddMinutes(-2))],
            TimeSpan.FromMinutes(40));

        Assert.Equal(ConductorMaxDurationStopVerdictKind.Defer, verdict.Kind);
    }

    [Xunit.Fact]
    public void SnapshotFailureDefersUntilCeilingThenExpiresLoudly()
    {
        var failure = new InvalidDataException("attempt metadata is unreadable");

        var deferred = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddMinutes(39),
            DeferralStartedAt,
            [],
            TimeSpan.FromMinutes(40),
            failure);
        var expired = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddMinutes(40),
            DeferralStartedAt,
            [],
            TimeSpan.FromMinutes(40),
            failure);
        var detail = ConductorMaxDurationStopDeferral.FormatStopDetail(
            TimeSpan.FromHours(12),
            expired,
            failure);

        Assert.Equal(ConductorMaxDurationStopVerdictKind.Defer, deferred.Kind);
        Assert.Equal(ConductorMaxDurationStopVerdictKind.StopExpired, expired.Kind);
        Assert.Contains("deferralExpired=true", detail, StringComparison.Ordinal);
        Assert.Contains("inflightStateUnavailable=true", detail, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(39, false)]
    [Xunit.InlineData(40, true)]
    public void WorkerAndAcceptanceShareOneCeiling(int elapsedMinutes, bool expired)
    {
        var worker = new ConductorMaxDurationInFlightAttempt(
            "worker:task-a:123", ["goal-a"], DeferralStartedAt,
            ConductorMaxDurationInFlightKind.WorkerDispatch);
        var acceptance = Attempt("attempt-a", DeferralStartedAt);

        foreach (var members in new[] { new[] { worker }, new[] { acceptance, worker } })
        {
            var verdict = ConductorMaxDurationStopDeferral.Decide(
                DeferralStartedAt.AddMinutes(elapsedMinutes), DeferralStartedAt,
                members, TimeSpan.FromMinutes(40));

            Assert.Equal(expired ? ConductorMaxDurationStopVerdictKind.StopExpired :
                ConductorMaxDurationStopVerdictKind.Defer, verdict.Kind);
            Assert.Contains(worker, verdict.Attempts);
            var announcement = ConductorMaxDurationStopDeferral.FormatDeferredEvent(
                2, DeferralStartedAt, DeferralStartedAt, TimeSpan.FromMinutes(40), members);
            Assert.Contains(" workers=worker:task-a:123", announcement, StringComparison.Ordinal);
            Assert.DoesNotContain("attempts=worker:", announcement, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void AcceptanceOnlyMessagesRemainUnchanged()
    {
        var attempts = new[] { Attempt("attempt-a", DeferralStartedAt.AddMinutes(-1)) };
        var announcement = ConductorMaxDurationStopDeferral.FormatDeferredEvent(
            2, DeferralStartedAt.AddSeconds(2), DeferralStartedAt,
            TimeSpan.FromSeconds(30), attempts);
        var expired = ConductorMaxDurationStopDeferral.Decide(
            DeferralStartedAt.AddSeconds(30), DeferralStartedAt,
            attempts, TimeSpan.FromSeconds(30));

        Assert.Equal(
            "LOOP_STOP_DEFERRED tick=2 reason=max-duration " +
            "deferral_started_at=2026-09-23T12:00:00.0000000+00:00 ceiling_ms=30000 " +
            "attempts=attempt-a attempt_elapsed_ms=attempt-a:62000 " +
            "attempt_started_at=attempt-a:2026-09-23T11:59:00.0000000+00:00",
            announcement);
        Assert.Equal("seconds=1 deferralExpired=true attempts=attempt-a",
            ConductorMaxDurationStopDeferral.FormatStopDetail(TimeSpan.FromSeconds(1), expired));
        Assert.Equal("seconds=1", ConductorMaxDurationStopDeferral.FormatStopDetail(
            TimeSpan.FromSeconds(1), new(ConductorMaxDurationStopVerdictKind.Stop, [])));
    }

    private static ConductorMaxDurationInFlightAttempt Attempt(string id, DateTimeOffset startedAt) =>
        new(id, ["goal-a"], startedAt);
}
