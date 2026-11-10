using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: fixture owns its fake time, sources, database, and output writers.
public sealed class ImpactReplayProgressTests
{
    [Fact]
    public void Startup_BeforeEnumeration_FlushesPhasesAndPreservesStdout()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.BeforeHistory = () =>
        {
            Assert.Contains("Startup", fixture.Error.Flushed);
            Assert.Contains("HistoryLoading", fixture.Error.Flushed);
            Assert.Equal(0, fixture.Status().Progress.Completed);
        };
        fixture.Source.BeforeInventory = () => Assert.Contains("InventoryLoading", fixture.Error.Flushed);

        Assert.Equal(0, fixture.Run());

        var events = fixture.Events();
        Assert.All(Enum.GetValues<ImpactReplayPhase>(), phase => Assert.Contains(events, e => e.Phase == phase));
        Assert.Null(events[0].DiscoveredInputs);
        Assert.Equal(2, events[^1].DiscoveredInputs);
        Assert.Equal(2, events[^1].ExaminedInputs);
        Assert.Equal(2, events[^1].ExaminedCases);
        Assert.Equal(2, events[^1].Completed);
        Assert.All(events, e => Assert.InRange(e.LastProgressElapsedSeconds, 0, e.RunElapsedSeconds));
        Assert.All(events, e => Assert.InRange(e.PhaseElapsedSeconds, 0, e.RunElapsedSeconds));
        Assert.Equal($"Impact replay: 2 attempts; reports: {fixture.Destination}{Environment.NewLine}", fixture.Output.ToString());
        Assert.Equal(fixture.Error.ToString(), fixture.Error.Flushed);
    }

    [Fact]
    public void Heartbeat_SlowPhase_PreservesCountsAndLastProgress()
    {
        using var fixture = new ImpactReplayObservationFixture();
        fixture.Source.OnCase = (attempt, _, _, _) =>
        {
            var before = fixture.Status().Progress;
            fixture.Clock.Advance(29);
            Assert.DoesNotContain(fixture.Events(), e => e.Kind == "heartbeat");
            fixture.Clock.Advance(1);
            var beat = fixture.Status().Progress;
            Assert.Equal("heartbeat", beat.Kind);
            Assert.Equal(ImpactReplayPhase.ImpactAnalysis, beat.Phase);
            Assert.Equal(attempt.AttemptId, beat.AttemptId);
            Assert.Equal(before.Completed, beat.Completed);
            Assert.Equal(before.LastProgressElapsedSeconds, beat.LastProgressElapsedSeconds);
            Assert.Equal(30, beat.PhaseElapsedSeconds);
            fixture.Clock.Advance(1);
            Assert.Single(fixture.Events(), e => e.Kind == "heartbeat");
            return ImpactReplayObservationFixture.Replayed(attempt.AttemptId);
        };
        fixture.Source.Attempts = [ImpactReplayObservationFixture.Attempt("slow")];

        Assert.Equal(0, fixture.Run());
        Assert.Equal(1, fixture.Status().Progress.Completed);
        Assert.Equal("completed", fixture.Status().Completion);
        var count = fixture.Events().Length;
        fixture.Clock.Advance(60);
        Assert.Equal(count, fixture.Events().Length); // disposed timer cannot publish late observations
    }
}
