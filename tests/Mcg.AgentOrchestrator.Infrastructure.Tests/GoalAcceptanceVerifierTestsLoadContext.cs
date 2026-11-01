using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsLoadContext : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact]
    public void EmitShardTimingProgressRecordsTypedLoadContextAndPreservesExistingFields()
    {
        var progress = new List<AcceptanceGateProgress>();
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(() => 3);
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 5);
        using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(37.5, 16, 425));
        var goalId = new GoalId("12345678123456781234567812345678");
        var elapsed = TimeSpan.FromSeconds(12);
        GoalAcceptanceVerifier.EmitShardTimingProgress(
            goalId,
            "shard-complete",
            "Process spawning",
            1,
            elapsed,
            3,
            DotnetBuildEnvironmentManager.CaptureStorageRoot(),
            progress.Add);

        var emitted = Assert.Single(progress);
        Assert.Equal(goalId.Value, emitted.GoalId);
        Assert.Equal("shard-complete", emitted.Phase);
        Assert.Equal("Process spawning", emitted.CurrentTarget);
        Assert.Equal(1, emitted.SlotIndex);
        Assert.Equal(elapsed, emitted.Elapsed);
        Assert.Null(emitted.PhaseBreakdown);
        var load = Assert.IsType<GateLoadContext>(emitted.LoadContext);
        AssertAvailable(load.ConcurrentShardCount, 3);
        AssertAvailable(load.HostCpuUtilizationPercent, 37.5);
        AssertAvailable(load.ProcessorCount, 16);
        AssertAvailable(load.CpuSampleWindowMilliseconds, 425);
        AssertAvailable(load.ConcurrentGateCount, 3);
        AssertAvailable(load.InFlightPaidWorkerDispatchCount, 5);
    }

    [Xunit.Fact]
    public void EmitShardTimingProgressDistinguishesAvailableZeroFromUnavailableCounts()
    {
        var progress = new List<AcceptanceGateProgress>();
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(() => null);
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 0);
        using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(0, 8, 250));

        GoalAcceptanceVerifier.EmitShardTimingProgress(
            null,
            "shards-complete",
            "0-infrastructure-shards",
            0,
            TimeSpan.Zero,
            0,
            DotnetBuildEnvironmentManager.CaptureStorageRoot(),
            progress.Add);

        var emitted = Assert.Single(progress);
        Assert.Equal("shards-complete", emitted.Phase);
        Assert.Equal("0-infrastructure-shards", emitted.CurrentTarget);
        Assert.Equal(0, emitted.SlotIndex);
        Assert.Equal(TimeSpan.Zero, emitted.Elapsed);
        Assert.Null(emitted.PhaseBreakdown);
        var load = Assert.IsType<GateLoadContext>(emitted.LoadContext);
        AssertAvailable(load.ConcurrentShardCount, 0);
        AssertAvailable(load.InFlightPaidWorkerDispatchCount, 0);
        AssertUnavailable(load.ConcurrentGateCount, "gate-count-unavailable");
        Assert.NotEqual(load.InFlightPaidWorkerDispatchCount.IsAvailable, load.ConcurrentGateCount.IsAvailable);
        Assert.NotEqual(load.InFlightPaidWorkerDispatchCount.Value, load.ConcurrentGateCount.Value);
    }

    [Xunit.Fact]
    public void ThrowingLoadProbeDoesNotEscapeOrPreventTimingRecord()
    {
        var progress = new List<AcceptanceGateProgress>();
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(
            () => throw new InvalidOperationException("deterministic gate probe failure"));
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 4);
        using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(25, 8, 500));

        var exception = Record.Exception(() => GoalAcceptanceVerifier.EmitShardTimingProgress(
            null,
            "shard-complete",
            "Cli",
            0,
            TimeSpan.FromSeconds(4),
            2,
            DotnetBuildEnvironmentManager.CaptureStorageRoot(),
            progress.Add));

        Assert.Null(exception);
        var emitted = Assert.Single(progress);
        Assert.Equal("Cli", emitted.CurrentTarget);
        Assert.Equal(TimeSpan.FromSeconds(4), emitted.Elapsed);
        var load = Assert.IsType<GateLoadContext>(emitted.LoadContext);
        AssertAvailable(load.ConcurrentShardCount, 2);
        AssertUnavailable(load.ConcurrentGateCount, "probe-error:InvalidOperationException");
        AssertAvailable(load.InFlightPaidWorkerDispatchCount, 4);
        AssertAvailable(load.HostCpuUtilizationPercent, 25);
    }

    [Xunit.Fact]
    public void LoadCaptureInvokesEachProbeOncePerLaneCompletion()
    {
        var gateCalls = 0;
        var workerCalls = 0;
        var cpuCalls = 0;
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(() =>
        {
            gateCalls++;
            return 1;
        });
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() =>
        {
            workerCalls++;
            return 0;
        });
        using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(() =>
        {
            cpuCalls++;
            return new GateLoadContextProbe.HostCpuSample(25, 4, 500);
        });

        var load = GateLoadContextProbe.Capture(1);

        Assert.Equal(1, gateCalls);
        Assert.Equal(1, workerCalls);
        Assert.Equal(1, cpuCalls);
        AssertAvailable(load.ConcurrentShardCount, 1);
    }

    [Xunit.Fact]
    public void CpuSamplesBelowMinimumWindowOrWithNonFiniteUtilizationAreUnavailable()
    {
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(() => 1);
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 0);
        using (GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(25, 4, 199)))
        {
            AssertUnavailable(
                GateLoadContextProbe.Capture(1).HostCpuUtilizationPercent,
                "cpu-sample-invalid");
        }

        using (GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(double.NaN, 4, 500)))
        {
            AssertUnavailable(
                GateLoadContextProbe.Capture(1).HostCpuUtilizationPercent,
                "cpu-sample-invalid");
        }
    }

    [Xunit.Fact]
    public void RealHostCpuProbeReturnsFiniteSampleOrExplicitUnavailableState()
    {
        using var gateProbe = GateLoadContextProbe.PushConcurrentGateCountProbe(() => 1);
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 0);

        var load = GateLoadContextProbe.Capture(1);

        Assert.Equal(load.HostCpuUtilizationPercent.IsAvailable, load.ProcessorCount.IsAvailable);
        Assert.Equal(load.HostCpuUtilizationPercent.IsAvailable, load.CpuSampleWindowMilliseconds.IsAvailable);
        if (load.HostCpuUtilizationPercent.IsAvailable)
        {
            Assert.True(double.IsFinite(load.HostCpuUtilizationPercent.Value!.Value));
            Assert.InRange(load.HostCpuUtilizationPercent.Value.Value, 0, 100);
            Assert.True(load.CpuSampleWindowMilliseconds.Value >= 200);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(load.HostCpuUtilizationPercent.UnavailableReason));
        }
    }

    [Xunit.Fact]
    public void ProductionGateProbeIncludesCallingGateWhenHeartbeatDirectoryIsReachable()
    {
        var heartbeatDirectory = Path.GetDirectoryName(GateHeartbeatArtifacts.GetStableSlotPath(0));
        Assert.False(string.IsNullOrWhiteSpace(heartbeatDirectory));
        Directory.CreateDirectory(heartbeatDirectory!);
        using var workerProbe = GateLoadContextProbe.PushInFlightWorkerDispatchProbe(() => 0);
        using var cpuProbe = GateLoadContextProbe.PushHostCpuProbe(
            () => new GateLoadContextProbe.HostCpuSample(25, 4, 500));

        var sample = GateLoadContextProbe.Capture(1).ConcurrentGateCount;

        Assert.True(sample.IsAvailable);
        Assert.NotNull(sample.Value);
        Assert.True(sample.Value >= 1);
        Assert.Null(sample.UnavailableReason);
    }

}
