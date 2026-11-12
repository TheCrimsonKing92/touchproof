using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: all intervals and elapsed time are fixture-local values.
public sealed class ActivationAdmissionOwnerIdleTrackerTests
{
    private static ActivationAdmissionSample Sample(string owners) =>
        new(1, false, 0, 2, 1, 5, owners.Length == 0 ? 0 : owners.Split(',').Length, owners);

    [Fact]
    public void Observe_OwnerAbsentOnSuccessfulCensus_StartsFreshInterval()
    {
        var elapsed = TimeSpan.Zero;
        var tracker = new ActivationAdmissionOwnerIdleTracker(() => elapsed);
        Assert.Null(tracker.Observe(Sample("train:one")));
        elapsed = TimeSpan.FromMinutes(9);
        // Successful absence represents own live gate, slot release, or no pending work.
        Assert.Null(tracker.Observe(Sample("")));
        Assert.Null(tracker.Observe(Sample("train:one")));
        elapsed = TimeSpan.FromMinutes(11);
        Assert.Null(tracker.Observe(Sample("train:one")));
        elapsed = TimeSpan.FromMinutes(20);
        Assert.Contains("owner=train:one", tracker.Observe(Sample("train:one"))!.Detail);
    }

    [Fact]
    public void Observe_ReplacementOwner_GetsItsOwnInterval()
    {
        var elapsed = TimeSpan.Zero;
        var tracker = new ActivationAdmissionOwnerIdleTracker(() => elapsed);
        tracker.Observe(Sample("train:one"));
        elapsed = TimeSpan.FromMinutes(9);
        tracker.Observe(Sample("train:two"));
        elapsed = TimeSpan.FromMinutes(11);
        Assert.Null(tracker.Observe(Sample("train:two")));
    }

    [Fact]
    public void Observe_UnrelatedOwnerProgress_DoesNotResetOldestOwner()
    {
        var elapsed = TimeSpan.Zero;
        var tracker = new ActivationAdmissionOwnerIdleTracker(() => elapsed);
        tracker.Observe(Sample("train:old"));
        elapsed = TimeSpan.FromMinutes(5);
        tracker.Observe(Sample("cohort:new,train:old"));
        elapsed = TimeSpan.FromMinutes(11);
        var verdict = tracker.Observe(Sample("train:old"));
        Assert.Contains("owner=train:old", verdict!.Detail);
        Assert.Contains("elapsedSeconds=660", verdict.Detail);
    }

    [Fact]
    public void Observe_SeveralOverdueOwners_SelectsLongestThenOrdinalIdentity()
    {
        var elapsed = TimeSpan.Zero;
        var tracker = new ActivationAdmissionOwnerIdleTracker(() => elapsed);
        tracker.Observe(Sample("train:z,cohort:a"));
        elapsed = TimeSpan.FromMinutes(1);
        tracker.Observe(Sample("train:z,cohort:a,cohort:younger"));
        elapsed = TimeSpan.FromMinutes(12);
        Assert.Contains("owner=cohort:a ", tracker.Observe(Sample("train:z,cohort:a,cohort:younger"))!.Detail);
        Assert.Contains("owner=train:z ", tracker.Observe(Sample("train:z,cohort:younger"))!.Detail);
    }

    [Fact]
    public void Observe_ClockEpochAndWallTime_DoNotChangeElapsedBoundary()
    {
        var elapsed = TimeSpan.FromDays(30);
        var tracker = new ActivationAdmissionOwnerIdleTracker(() => elapsed);
        tracker.Observe(Sample("cohort:one"));
        elapsed += ActivationAdmissionHealthPolicy.OwnerIdleLimit;
        Assert.Null(tracker.Observe(Sample("cohort:one")));
        elapsed += TimeSpan.FromTicks(1);
        Assert.Equal(ConductorActivationRevertReason.AdmissionOwnerIdle,
            tracker.Observe(Sample("cohort:one"))!.Reason);
    }
}
