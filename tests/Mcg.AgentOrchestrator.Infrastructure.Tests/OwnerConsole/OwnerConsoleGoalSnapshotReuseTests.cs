using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its queries, source snapshots and clock.
public sealed class OwnerConsoleGoalSnapshotReuseTests
{
    [Fact]
    public async Task UnchangedSnapshotLoadsOnce_ChangedSnapshotLoadsTwice_RowsUseRefreshTime()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        OwnerConsoleViewModel first;
        using (fixture.Snapshots.BeginRefresh(reuseUnchanged: true)) first = await fixture.Builder.BuildBoardAsync();
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
        fixture.State.Versions["22222222"] = "b";
        OwnerConsoleViewModel second;
        using (fixture.Snapshots.BeginRefresh(reuseUnchanged: true)) second = await fixture.Builder.BuildBoardAsync();

        Assert.Equal(1, fixture.State.Loads["11111111"]);
        Assert.Equal(2, fixture.State.Loads["22222222"]);
        Assert.All(first.Board, row => { Assert.Equal(TimeSpan.FromMinutes(2), row.Age); Assert.Equal("gate running 2m", row.Stage); });
        Assert.All(second.Board, row => { Assert.Equal(TimeSpan.FromMinutes(5), row.Age); Assert.Equal("gate running 5m", row.Stage); });
        Assert.Equal(3, fixture.Snapshots.StoreLoadedGoals);
    }

    [Fact]
    public async Task UnequalBackwardVersionAndReappearingGoalAreReloaded()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        async Task Read()
        {
            using var generation = fixture.Snapshots.BeginRefresh(reuseUnchanged: true);
            await fixture.Builder.BuildBoardAsync();
        }
        await Read();
        fixture.State.Versions["11111111"] = "0";
        await Read();
        fixture.State.Visible.Remove("11111111");
        await Read();
        fixture.State.Visible.Add("11111111");
        await Read();
        Assert.Equal(3, fixture.State.Loads["11111111"]);
        Assert.Equal(1, fixture.State.Loads["22222222"]);
    }

    [Fact]
    public async Task FailedReadIsRetriedWithoutAdvancingCache()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        fixture.State.FailLoad = true;
        using (fixture.Snapshots.BeginRefresh(reuseUnchanged: true))
            await Assert.ThrowsAsync<IOException>(() => fixture.Builder.BuildBoardAsync());
        fixture.State.FailLoad = false;
        using (fixture.Snapshots.BeginRefresh(reuseUnchanged: true)) await fixture.Builder.BuildBoardAsync();
        Assert.Equal(2, fixture.Snapshots.StoreLoadedGoals);
        Assert.Equal(1, fixture.State.Loads["11111111"]);
    }
}
