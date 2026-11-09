using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: the scan, clock, coordinator and headless app belong to each case.
public sealed class OwnerConsoleLandedTodayScanTests
{
    [Fact]
    public async Task ScansOnlyAtStartupAndLocalRollover_FoldedLandingRaisesPublishedCount()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        fixture.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("scan-test", TimeSpan.FromHours(-6), "scan-test", "scan-test");
        fixture.Clock.Now = new(2026, 1, 2, 5, 59, 0, TimeSpan.Zero);
        fixture.ScanCount = 3;
        var refresh = fixture.Coordinator();
        await refresh.RefreshAsync();
        await refresh.RefreshAsync(skipIfBusy: true);
        Assert.Equal(1, fixture.ScanReads);
        Assert.Equal(3, fixture.Controller!.Model!.Status.LandedToday);
        var landing = new OwnerConductEvent(fixture.Clock.Now, "loop-relaunch", "33333333", "LOOP_RELAUNCH_NOT_REQUIRED");
        await refresh.RefreshAsync([landing, landing]);
        Assert.Equal(4, fixture.Controller.Model.Status.LandedToday);
        Assert.Equal(1, fixture.ScanReads);
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(2);
        fixture.ScanCount = 1;
        var boards = fixture.State.Boards;
        await refresh.RefreshAsync([new(fixture.Clock.Now, "tick", null, "TICK")], scope: OwnerConsoleRefreshScope.None);
        Assert.Equal(2, fixture.ScanReads);
        Assert.Equal(1, fixture.Controller.Model.Status.LandedToday);
        Assert.Equal(boards, fixture.State.Boards);
        await refresh.RefreshAsync();
        Assert.Equal(2, fixture.ScanReads);
    }

    [Fact]
    public async Task StartupHistorySeedsExistingScan_WithoutAnotherSameDayScan()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        var refresh = fixture.Coordinator();
        refresh.Seed(new([], null, 7));
        await refresh.RefreshAsync();
        Assert.Equal(0, fixture.ScanReads);
        Assert.Equal(7, fixture.Controller!.Model!.Status.LandedToday);
    }

    [Fact]
    public async Task LandingAlreadyInRolloverScanIsNotAddedTwice()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        var refresh = fixture.Coordinator();
        await refresh.RefreshAsync();
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        fixture.ScanCount = 1;
        var landing = new OwnerConductEvent(fixture.Clock.Now, "loop-relaunch", "33333333", "LOOP_RELAUNCH_NOT_REQUIRED");
        await refresh.RefreshAsync([landing]);
        Assert.Equal(1, fixture.Controller!.Model!.Status.LandedToday);
        await refresh.RefreshAsync([landing]);
        Assert.Equal(1, fixture.Controller.Model.Status.LandedToday);
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        await refresh.RefreshAsync([landing with { Timestamp = fixture.Clock.Now, GoalId = "44444444" }]);
        Assert.Equal(2, fixture.Controller.Model.Status.LandedToday);
    }

    [Fact]
    public async Task ScanMembershipReconcilesLateDeliveries_AcrossSeparateRefreshes()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        var directory = Directory.CreateTempSubdirectory("owner-landings-");
        try
        {
            var path = Path.Combine(directory.FullName, "landed.jsonl");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                eventType = "GoalLanded", goalId = "33333333", timestamp = fixture.Clock.Now
            }) + "\n");
            var scan = new OwnerConsoleLandedTodayScan(directory.FullName, fixture.Clock);
            Assert.Equal(OwnerConsoleLandedToday.Count(directory.FullName, fixture.Clock), scan.Current());
            var refresh = fixture.Coordinator(scan);
            await refresh.RefreshAsync();
            var alreadyScanned = new OwnerConductEvent(fixture.Clock.Now, "loop-relaunch", "33333333", "LOOP_RELAUNCH_NOT_REQUIRED");
            await refresh.RefreshAsync([alreadyScanned]);
            Assert.Equal(1, fixture.Controller!.Model!.Status.LandedToday);
            Assert.Single(refresh.CountedLandings); // Event identity remains recorded, even when the scan covers its count.
            await refresh.RefreshAsync([alreadyScanned with { GoalId = "44444444" }]);
            Assert.Equal(2, fixture.Controller.Model.Status.LandedToday);
            fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
            await refresh.RefreshAsync();
            Assert.Equal(0, fixture.Controller.Model.Status.LandedToday);
            Assert.Empty(refresh.CountedLandings);
        }
        finally { directory.Delete(recursive: true); }
    }
}
