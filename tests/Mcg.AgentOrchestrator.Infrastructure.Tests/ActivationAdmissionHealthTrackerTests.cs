using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: all samples and timestamps are values; no external resources.
public sealed class ActivationAdmissionHealthTrackerTests
{
    [Xunit.Fact]
    public void EvidenceHeldPermits_RoundTrips()
    {
        var sample = new ActivationAdmissionSample(1, false, 5, 1, 0, 2);
        Assert.True(ActivationAdmissionSample.TryParse(sample.Format(), out var parsed));
        Assert.Equal(sample, parsed);
        Assert.Equal(2, parsed.EvidenceHeldPermits);
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" ts=2026-10-25T00:00:00Z evidenceHeld=2")]
    public void OlderHealthLines_DefaultEvidenceHeldToZero(string suffix)
    {
        Assert.True(ActivationAdmissionSample.TryParse(
            "ACTIVATION_ADMISSION_HEALTH tick=1 census=ok waiting=5 occupied=1 live=0" + suffix, out var sample));
        Assert.Equal(0, sample.EvidenceHeldPermits);
    }

    [Xunit.Theory]
    [Xunit.InlineData("x")]
    [Xunit.InlineData("")]
    [Xunit.InlineData("2147483648")]
    public void MalformedEvidenceHeld_IsRejected(string value) => Assert.False(ActivationAdmissionSample.TryParse(
        "ACTIVATION_ADMISSION_HEALTH tick=1 census=ok waiting=5 occupied=1 live=0 evidenceHeld=" + value, out _));

    [Xunit.Fact]
    public void AllPermitsEvidenceHeld_StayHealthyAndResetStreak()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick <= 11; tick++)
            tracker.Observe(new(tick, false, 5, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds((tick - 1) * 22));
        for (var tick = 12; tick <= 24; tick++)
            Assert.Equal(ActivationAdmissionHealthState.Healthy,
                tracker.Observe(new(tick, false, 5, 1, 0, 2), DateTimeOffset.UnixEpoch.AddSeconds((tick - 1) * 22)).State);
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
            tracker.Observe(new(25, false, 5, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds(528)).State);
    }

    [Xunit.Fact]
    public void OneEvidenceHeldPermit_TripsExactlyLikeZero()
    {
        ActivationAdmissionHealthVerdict Replay(int held)
        {
            var tracker = new ActivationAdmissionHealthTracker();
            for (var tick = 1; tick <= 12; tick++)
                tracker.Observe(new(tick, false, 5, 1, 0, held), DateTimeOffset.UnixEpoch.AddSeconds((tick - 1) * 22));
            return tracker.Verdict;
        }
        var verdict = Replay(1);
        Assert.Equal(Replay(0), verdict);
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(ConductorActivationRevertReason.AdmissionStalled, verdict.Reason);
        Assert.Equal("occupiedNoLiveGateTicks=12 waiting=5 occupied=1 live=0 elapsedSeconds=242", verdict.Detail);
    }

    [Xunit.Fact]
    public void EvidenceHeldPermits_DoNotExemptCensusFailure()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick <= 8; tick++)
            tracker.Observe(new(tick, true, 5, 1, 0, 2), DateTimeOffset.UnixEpoch.AddSeconds((tick - 1) * 43));
        Assert.Equal(ConductorActivationRevertReason.AdmissionCensusFailing, tracker.Verdict.Reason);
    }

    [Xunit.Fact]
    public void CensusFailures_AtElapsedAllowance_TripAndLatch()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick < 8; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen, Observe(tracker, tick, failed: true).State);
        var verdict = Observe(tracker, 8, failed: true);
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(ConductorActivationRevertReason.AdmissionCensusFailing, verdict.Reason);
        Assert.Equal("censusFailedTicks=8 firstTick=1 lastTick=8 elapsedSeconds=301", verdict.Detail);
        Assert.Same(verdict, tracker.Observe(new(9, false, 0, 0, 0), DateTimeOffset.UnixEpoch.AddSeconds(344)));
    }

    [Xunit.Fact]
    public void SuccessfulCensus_BreaksFailureStreak_ResetsCount()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        var belowLimit = 7;
        for (var tick = 1; tick <= belowLimit; tick++) Observe(tracker, tick, failed: true);
        Assert.Equal(ActivationAdmissionHealthState.Healthy,
            tracker.Observe(new(8, false, 0, 0, 0), DateTimeOffset.UnixEpoch.AddSeconds(301)).State);
        for (var offset = 1; offset <= belowLimit; offset++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                Observe(tracker, belowLimit + 1 + offset, failed: true).State);
        var verdict = Observe(tracker, 16, failed: true);
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal("censusFailedTicks=8 firstTick=9 lastTick=16 elapsedSeconds=301", verdict.Detail);
    }

    [Xunit.Fact]
    public void OccupiedWithoutLiveGate_AtElapsedAllowance_TripsWithLastSample()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick < 12; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                Observe(tracker, tick, waiting: 2, occupied: 13).State);
        var verdict = Observe(tracker, 12,
            waiting: 2, occupied: 13);
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(ConductorActivationRevertReason.AdmissionStalled, verdict.Reason);
        Assert.Equal("occupiedNoLiveGateTicks=12 waiting=2 occupied=13 live=0 elapsedSeconds=242", verdict.Detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "AdmissionCensusFailing", 300)]
    [Xunit.InlineData(false, "AdmissionStalled", 234)]
    public void Streak_AtExactAllowance_TripsWithMinimumObservations(bool failed, string reason, int seconds)
    {
        var tracker = new ActivationAdmissionHealthTracker();
        var start = DateTimeOffset.UnixEpoch;
        ActivationAdmissionSample Sample(int tick) => new(tick, failed, 2, 13, 0);
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen, tracker.Observe(Sample(41), start).State);
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
            tracker.Observe(Sample(42), start.AddSeconds(seconds) - TimeSpan.FromTicks(1)).State);
        var verdict = tracker.Observe(Sample(43), start.AddSeconds(seconds));
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(reason, verdict.Reason.ToString());
        Assert.EndsWith($" elapsedSeconds={seconds}", verdict.Detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, 300)]
    [Xunit.InlineData(false, 234)]
    public void Streak_JustBeforeAllowance_StaysOpenDespiteEnoughObservations(bool failed, int seconds)
    {
        var tracker = new ActivationAdmissionHealthTracker();
        var start = DateTimeOffset.UnixEpoch;
        tracker.Observe(new(1, failed, 3, 1, 0), start);
        tracker.Observe(new(2, failed, 3, 1, 0), start.AddSeconds(1));
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
            tracker.Observe(new(3, failed, 3, 1, 0), start.AddSeconds(seconds).AddTicks(-1)).State);
        var verdict = tracker.Observe(new(4, failed, 3, 1, 0), start.AddSeconds(seconds));
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.EndsWith($" elapsedSeconds={seconds}", verdict.Detail);
    }

    [Xunit.Fact]
    public void OccupiedWithoutLiveGate_EightSecondTicks_TripsAt240Seconds()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick <= 30; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                tracker.Observe(new(tick, false, 3, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds((tick - 1) * 8)).State);
        var verdict = tracker.Observe(new(31, false, 3, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds(240));
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(ConductorActivationRevertReason.AdmissionStalled, verdict.Reason);
        Assert.Equal("occupiedNoLiveGateTicks=31 waiting=3 occupied=1 live=0 elapsedSeconds=240", verdict.Detail);
    }

    [Xunit.Fact]
    public void OccupiedWithoutLiveGate_SixtySecondTicks_ReportsCountAndElapsed()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick <= 4; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                tracker.Observe(new(tick, false, 3, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds((tick - 1) * 60)).State);
        var verdict = tracker.Observe(new(5, false, 3, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds(240));
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(ConductorActivationRevertReason.AdmissionStalled, verdict.Reason);
        Assert.Equal("occupiedNoLiveGateTicks=5 waiting=3 occupied=1 live=0 elapsedSeconds=240", verdict.Detail);
    }

    [Xunit.Fact]
    public void CensusFailures_EightSecondTicks_WaitForFiveMinutes()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick <= 38; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                tracker.Observe(new(tick, true, 3, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds((tick - 1) * 8)).State);
        var verdict = tracker.Observe(new(39, true, 3, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds(304));
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(ConductorActivationRevertReason.AdmissionCensusFailing, verdict.Reason);
        Assert.Equal("censusFailedTicks=39 firstTick=1 lastTick=39 elapsedSeconds=304", verdict.Detail);
    }

    [Xunit.Fact]
    public void HealthySample_BetweenOccupiedSamples_RestartsStreakAndAllowance()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick <= 4; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                tracker.Observe(new(tick, false, 3, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds((tick - 1) * 60)).State);
        Assert.Equal(ActivationAdmissionHealthState.Healthy,
            tracker.Observe(new(5, false, 3, 1, 1), DateTimeOffset.UnixEpoch.AddSeconds(200)).State);
        for (var tick = 6; tick <= 9; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                tracker.Observe(new(tick, false, 3, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds(240 + (tick - 6) * 60)).State);
        var verdict = tracker.Observe(new(10, false, 3, 1, 0), DateTimeOffset.UnixEpoch.AddSeconds(480));
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal("occupiedNoLiveGateTicks=5 waiting=3 occupied=1 live=0 elapsedSeconds=240", verdict.Detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "censusFailedTicks=3 firstTick=1 lastTick=3")]
    [Xunit.InlineData(false, "occupiedNoLiveGateTicks=3 waiting=3 occupied=1 live=0")]
    public void Streak_TwoSamplesAcrossLongPause_WaitsForThird(bool failed, string detail)
    {
        var tracker = new ActivationAdmissionHealthTracker();
        var start = DateTimeOffset.UnixEpoch;
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
            tracker.Observe(new(1, failed, 3, 1, 0), start).State);
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
            tracker.Observe(new(2, failed, 3, 1, 0), start.AddMinutes(10)).State);
        var verdict = tracker.Observe(new(3, failed, 3, 1, 0), start.AddSeconds(601));
        Assert.Equal(ActivationAdmissionHealthState.Tripped, verdict.State);
        Assert.Equal(detail + " elapsedSeconds=601", verdict.Detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData(3, 1, 1)]
    [Xunit.InlineData(0, 13, 0)]
    [Xunit.InlineData(3, 0, 0)]
    public void HealthyControls_ForFortyTicks_StayHealthy(int waiting, int occupied, int live)
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick <= 40; tick++)
            Assert.Equal(ActivationAdmissionHealthState.Healthy,
                Observe(tracker, tick, waiting: waiting, occupied: occupied, live: live).State);
    }

    [Xunit.Fact]
    public void IsolatedFailure_BetweenHealthySamples_DoesNotTrip()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        Assert.Equal(ActivationAdmissionHealthState.Healthy, Observe(tracker, 1).State);
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen, Observe(tracker, 2, failed: true).State);
        Assert.Equal(ActivationAdmissionHealthState.Healthy, Observe(tracker, 3).State);
    }

    [Xunit.Fact]
    public void SwitchingStreakKind_ResetsCountAndElapsedStart()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick < 8; tick++)
            Observe(tracker, tick, failed: true);
        var now = DateTimeOffset.UnixEpoch.AddMinutes(5);
        for (var tick = 8; tick <= 10; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                tracker.Observe(new(tick, false, 2, 13, 0), now.AddSeconds(tick - 8)).State);
        for (var tick = 11; tick <= 13; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                tracker.Observe(new(tick, true, 2, 13, 0), now.AddMinutes(5).AddSeconds(tick - 11)).State);
    }

    [Xunit.Theory]
    [Xunit.InlineData("ACTIVATION_ADMISSION_HEALTH tick=1 census=unknown waiting=2 occupied=13 live=0")]
    [Xunit.InlineData("ACTIVATION_ADMISSION_HEALTH tick=1 census=failed waiting=2 occupied=13")]
    [Xunit.InlineData("ACTIVATION_ADMISSION_HEALTH tick=1 census=failed waiting=2 occupied=13 live=-1")]
    [Xunit.InlineData("ACTIVATION_ADMISSION_HEALTH tick=bad census=failed waiting=2 occupied=13 live=0")]
    public void MalformedLine_IsIgnored(string line) => Assert.False(ActivationAdmissionSample.TryParse(line, out _));

    [Xunit.Fact]
    public void FormattedSample_WithProgressSuffix_RoundTrips()
    {
        var sample = new ActivationAdmissionSample(41, true, 2, 13, 0);
        Assert.True(ActivationAdmissionSample.TryParse(sample.Format() + " ts=2026-10-16T00:00:00Z", out var parsed));
        Assert.Equal(sample, parsed);
    }

    private static ActivationAdmissionHealthVerdict Observe(ActivationAdmissionHealthTracker tracker,
        int tick, bool failed = false, int waiting = 0, int occupied = 0, int live = 0) =>
        tracker.Observe(new(tick, failed, waiting, occupied, live),
            DateTimeOffset.UnixEpoch.AddSeconds((tick - 1) * (failed ? 43 : 22)));
}
