using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorDrainSignalTests
{
    [Fact]
    public void ObservedDeadlineSurvivesReplacementAndDeletion()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = ConductorDrainSignal.PathFor(fixture.Workspace.ConductorStopFilePath);
        var clock = new DrainClock();
        var signal = new ConductorDrainSignal(path, clock, ignoreInitial: false);
        ConductorDrainSignal.Write(path, 2.5);

        Assert.True(signal.Observe());
        Assert.Equal(TimeSpan.FromSeconds(2.5), signal.BoundWait(TimeSpan.FromMinutes(1)));

        clock.Timestamp = TimeSpan.FromSeconds(1).Ticks;
        ConductorDrainSignal.Write(path, 60);
        Assert.False(signal.Observe());
        Assert.Equal(TimeSpan.FromSeconds(1.5), signal.BoundWait(TimeSpan.FromMinutes(1)));
        File.Delete(path);

        clock.Timestamp = TimeSpan.FromSeconds(2.5).Ticks;
        Assert.True(signal.IsDraining);
        Assert.True(signal.IsExpired);
    }

    [Fact]
    public void MalformedRequestUsesDefaultAndReportsTheFallback()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = ConductorDrainSignal.PathFor(fixture.Workspace.ConductorStopFilePath);
        File.WriteAllText(path, "{");
        var signal = new ConductorDrainSignal(path, new DrainClock(), ignoreInitial: false);

        Assert.True(signal.Observe());
        Assert.Equal(TimeSpan.FromMinutes(30), signal.BoundWait(TimeSpan.FromHours(1)));
        Assert.EndsWith(" malformed=true", signal.FormatProgress(0, new([])));
    }

    [Fact]
    public void ScopedWatchObservesReplacementOfAnInitialRequest()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = ConductorDrainSignal.PathFor(fixture.Workspace.ConductorStopFilePath);
        ConductorDrainSignal.Write(path, 1);
        var signal = new ConductorDrainSignal(path, new DrainClock(), ignoreInitial: true);
        Assert.False(signal.Observe());

        ConductorDrainSignal.Write(path, 12);
        Assert.True(signal.Observe());
        Assert.Equal(TimeSpan.FromSeconds(12), signal.BoundWait(TimeSpan.FromHours(1)));
    }

    private sealed class DrainClock : TimeProvider
    {
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
