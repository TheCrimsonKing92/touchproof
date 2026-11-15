using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repositories, owned event-gated children, async-flow QoS overrides.
public sealed class SelectedExecutionBackstopGateRunnerQosTests : HostCapacityBoundTestBase
{
    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_PolicySnapshot_ControlsRootMembersAndFinalTotals(bool enabled)
    {
        using var attemptFlag = AcceptanceChildQos.PushForTests(!enabled);
        using var fixture = new HostedVerifierQosFixture(enabled) { FailMemberSetter = true };
        Assert.Equal("green", (await fixture.RunAsync()).Result);
        fixture.AssertLane(Assert.Single(fixture.Lanes), enabled);
        Assert.Equal($"BUILD_QOS kind=full-suite opted_out={(enabled ? 2 : 0)} tried={(enabled ? 3 : 0)}",
            Assert.Single(fixture.Summaries()));
        Assert.Equal(!enabled, AcceptanceChildQos.Enabled);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_PolicyChanges_BothOrdersTakeEffectWithoutRelaunch(bool first)
    {
        using var attemptFlag = AcceptanceChildQos.PushForTests(false);
        using var fixture = new HostedVerifierQosFixture(first);
        Assert.Equal("green", (await fixture.RunAsync()).Result);
        fixture.WritePolicy(!first);
        fixture.ExpectedEnabled = !first;
        Assert.Equal("green", (await fixture.RunAsync()).Result);
        var lanes = fixture.Lanes.ToArray();
        Assert.Equal(2, lanes.Length);
        fixture.AssertLane(lanes[0], first);
        fixture.AssertLane(lanes[1], !first);
        Assert.Equal(new[] { first, !first }.Select(enabled =>
            $"BUILD_QOS kind=full-suite opted_out={(enabled ? 3 : 0)} tried={(enabled ? 3 : 0)}"), fixture.Summaries());
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Run_OverlappingEnabledAndDisabled_DecisionsStayIsolated()
    {
        using var attemptFlag = AcceptanceChildQos.PushForTests(false);
        using var enabled = new HostedVerifierQosFixture(true);
        using var disabled = new HostedVerifierQosFixture(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        enabled.BeforeLane = async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(TestHangGuard.HangSafetyBound);
        };
        var active = enabled.RunAsync();
        try
        {
            await entered.Task.WaitAsync(TestHangGuard.HangSafetyBound);
            Assert.Equal("green", (await disabled.RunAsync()).Result);
            disabled.AssertLane(Assert.Single(disabled.Lanes), false);
        }
        finally { release.TrySetResult(); await active; }
        enabled.AssertLane(Assert.Single(enabled.Lanes), true);
        Assert.Equal("BUILD_QOS kind=full-suite opted_out=3 tried=3", Assert.Single(enabled.Summaries()));
        Assert.Equal("BUILD_QOS kind=full-suite opted_out=0 tried=0", Assert.Single(disabled.Summaries()));
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_ExceptionOrCancellation_StillEmitsOneSummary(bool cancelled)
    {
        using var fixture = new HostedVerifierQosFixture(true)
            { ThrowAfterLane = !cancelled, CancelAfterLane = cancelled };
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync());
        else await Assert.ThrowsAsync<IOException>(() => fixture.RunAsync());
        fixture.AssertLane(Assert.Single(fixture.Lanes), true);
        Assert.Equal("BUILD_QOS kind=full-suite opted_out=3 tried=3", Assert.Single(fixture.Summaries()));
    }
}
