using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: sample replay and a private monotonic clock, without external work.
public sealed class ActivationAdmissionPreparingOwnerHealthTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Observe_MeasuredTrainGap_StaysHealthyThroughNextGate()
    {
        var elapsed = TimeSpan.Zero;
        var tracker = new ActivationAdmissionHealthTracker(() => elapsed);
        Assert.Equal(ActivationAdmissionHealthState.Healthy,
            tracker.Observe(new(1, false, 5, 1, 1), Start).State);
        for (var tick = 2; tick <= 13; tick++)
        {
            elapsed += TimeSpan.FromSeconds(12 + tick % 4);
            var sample = new ActivationAdmissionSample(tick, false, 5, 1, 0, 0, 1, "train:survivors");
            Assert.True(ActivationAdmissionSample.TryParse(sample.Format(), out var parsed));
            Assert.Equal(ActivationAdmissionHealthState.Healthy, tracker.Observe(parsed, Start + elapsed).State);
        }
        Assert.Equal(ActivationAdmissionHealthState.Healthy,
            tracker.Observe(new(14, false, 5, 1, 1), Start + elapsed).State);
    }

    [Fact]
    public void Observe_OwnerlessSlot_TripsWithUnchangedDetail()
    {
        var tracker = new ActivationAdmissionHealthTracker();
        for (var tick = 1; tick <= 12; tick++)
            tracker.Observe(new(tick, false, 5, 1, 0), Start.AddSeconds(tick * 13));
        Assert.Equal(ConductorActivationRevertReason.AdmissionStalled, tracker.Verdict.Reason);
        Assert.Equal("occupiedNoLiveGateTicks=12 waiting=5 occupied=1 live=0", tracker.Verdict.Detail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cohort:one")]
    public void Observe_PreparingTick_ResetsTickAndElapsedStreak(string owners)
    {
        var tracker = new ActivationAdmissionHealthTracker(() => TimeSpan.Zero);
        for (var tick = 1; tick <= 11; tick++)
            tracker.Observe(new(tick, false, 5, 1, 0), Start.AddSeconds(tick));
        Assert.Equal(ActivationAdmissionHealthState.Healthy,
            tracker.Observe(new(12, false, 5, 1, 0, 0, 1, owners), Start.AddMinutes(4)).State);
        for (var tick = 13; tick <= 23; tick++)
            Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
                tracker.Observe(new(tick, false, 5, 1, 0), Start.AddMinutes(8).AddSeconds(tick)).State);
        Assert.Equal(ConductorActivationRevertReason.AdmissionStalled,
            tracker.Observe(new(24, false, 5, 1, 0), Start.AddMinutes(8).AddSeconds(24)).Reason);
    }

    [Fact]
    public void Observe_OwnerBeyondTenMinutes_TripsAndLatchesOwnerIdle()
    {
        var elapsed = TimeSpan.Zero;
        var tracker = new ActivationAdmissionHealthTracker(() => elapsed);
        var sample = new ActivationAdmissionSample(1, false, 0, 1, 1, 10, 1, "train:survivors");
        Assert.Equal(ActivationAdmissionHealthState.Healthy, tracker.Observe(sample, Start).State);
        elapsed = TimeSpan.FromMinutes(10);
        Assert.Equal(ActivationAdmissionHealthState.Healthy, tracker.Observe(sample, Start).State);
        elapsed += TimeSpan.FromSeconds(1);
        var verdict = tracker.Observe(sample, Start);
        Assert.Equal(ConductorActivationRevertReason.AdmissionOwnerIdle, verdict.Reason);
        Assert.Contains("owner=train:survivors", verdict.Detail);
        Assert.Contains("elapsedSeconds=601", verdict.Detail);
        Assert.Contains("limitSeconds=600", verdict.Detail);
        Assert.Same(verdict, tracker.Observe(new(2, false, 0, 0, 0), Start));
    }

    [Fact]
    public void Observe_FailedCensus_PreservesIntervalAndFailurePrecedence()
    {
        var elapsed = TimeSpan.Zero;
        var tracker = new ActivationAdmissionHealthTracker(() => elapsed);
        var sample = new ActivationAdmissionSample(1, false, 0, 1, 0, 0, 1, "cohort:one");
        tracker.Observe(sample, Start);
        elapsed = TimeSpan.FromMinutes(11);
        Assert.Equal(ActivationAdmissionHealthState.StreakOpen,
            tracker.Observe(sample with { CensusFailed = true, PreparingOwners = "" }, Start).State);
        Assert.Equal(ConductorActivationRevertReason.AdmissionOwnerIdle, tracker.Observe(sample, Start).Reason);

        tracker = new(() => elapsed);
        for (var tick = 1; tick <= 8; tick++)
            tracker.Observe(sample with { Tick = tick, CensusFailed = true }, Start.AddSeconds(tick));
        Assert.Equal(ConductorActivationRevertReason.AdmissionCensusFailing, tracker.Verdict.Reason);
    }
}
