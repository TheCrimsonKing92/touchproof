using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: diagnostics and store-load counts are local to each fixture.
public sealed class OwnerConsoleRefreshTriggerTimingsTests
{
    [Fact]
    public async Task TimingsLineAttributesExecutedTriggersAndActualStoreLoads()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        var refresh = fixture.Coordinator();
        refresh.Seed(new([], null, 0));
        await refresh.RefreshAsync();
        await refresh.RefreshAsync(skipIfBusy: true);
        await refresh.RefreshAsync([new(fixture.Clock.Now, "tick", null, "TICK")], scope: OwnerConsoleRefreshScope.None);
        await refresh.RefreshAsync([], scope: OwnerConsoleRefreshScope.For(["11111111"]));
        await refresh.RefreshAsync([new(fixture.Clock.Now, "tick", null, "TICK")], scope: OwnerConsoleRefreshScope.All);
        Assert.Contains("trigger-startup=1", fixture.TimingLine);
        Assert.Contains("trigger-periodic=1", fixture.TimingLine);
        Assert.Contains("trigger-conductor-event=2", fixture.TimingLine);
        Assert.Contains("trigger-change-stream=2", fixture.TimingLine);
        Assert.Contains("trigger-owner-command=1", fixture.TimingLine);
        Assert.Contains("goal-snapshots-loaded=2", fixture.TimingLine);
        Assert.Equal(fixture.State.Loads.Values.Sum(), fixture.Snapshots.StoreLoadedGoals);
        Assert.Equal(4, fixture.State.Boards); // The conductor-only heartbeat did no board work.

        fixture.State.Versions["22222222"] = "changed";
        await refresh.RefreshAsync();
        Assert.Contains("trigger-owner-command=2", fixture.TimingLine);
        Assert.Contains("goal-snapshots-loaded=3", fixture.TimingLine);
        Assert.Equal(1, fixture.State.Loads["11111111"]);
        Assert.Equal(2, fixture.State.Loads["22222222"]);
        Assert.Equal(fixture.State.Loads.Values.Sum(), fixture.Snapshots.StoreLoadedGoals);
    }

    [Fact]
    public async Task BusySkippedPeriodicRefreshIsNotCounted()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        var refresh = fixture.Coordinator();
        refresh.HoldForStartup();
        await refresh.RefreshAsync(skipIfBusy: true);
        Assert.Contains("trigger-periodic=0", fixture.Timings.Line(0));
        Assert.Null(fixture.TimingLine);
        refresh.Release();
    }
}
