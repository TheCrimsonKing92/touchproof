using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repository, event-gated lane commands and async-flow seams.
public sealed class SelectedExecutionBackstopFailureRerunQosTests : HostCapacityBoundTestBase
{
    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_FailureRerun_InheritsSnapshotAndContributesToTotals(bool enabled)
    {
        using var attemptFlag = AcceptanceChildQos.PushForTests(!enabled);
        using var fixture = new HostedVerifierQosFixture(enabled) { FailFirst = true };
        fixture.BeforeRerun = () => fixture.WritePolicy(!enabled);
        var result = await fixture.RunAsync();
        Assert.Equal("green", result.Result);
        Assert.Equal(1, fixture.FocusedCalls);
        Assert.Equal(2, fixture.Lanes.Count);
        Assert.All(fixture.Lanes, lane => fixture.AssertLane(lane, enabled));
        Assert.Equal($"BUILD_QOS kind=full-suite opted_out={(enabled ? 6 : 0)} tried={(enabled ? 6 : 0)}",
            Assert.Single(fixture.Summaries()));
    }
}
