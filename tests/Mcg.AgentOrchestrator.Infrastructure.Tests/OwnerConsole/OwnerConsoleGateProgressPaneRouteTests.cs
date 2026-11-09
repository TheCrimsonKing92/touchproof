using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its coordinator, headless view and fake clock.
public sealed class OwnerConsoleGateProgressPaneRouteTests
{
    [Fact]
    public async Task Refresh_ProgressBatches_ReadOnlyPanesUntilGoalChanges()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture(marker: false);
        var refresh = fixture.Coordinator();
        await refresh.RefreshAsync();
        Assert.Equal(1, fixture.State.Boards);
        Assert.Equal(1, fixture.Source.Reads);
        Assert.Equal(1, Calls(fixture, "activity"));
        Assert.Equal(1, Calls(fixture, "decisions"));
        Assert.Equal(1, fixture.GateReads);

        OwnerConductEvent Item(string kind, string detail = "") =>
            new(fixture.Clock.Now, kind, "11111111", detail);
        OwnerConductEvent[][] batches =
        [
            [Item("gate-progress", "lane=a"), Item("gate-progress", "lane=b"), Item("gate-progress", "lane=c")],
            [Item("remote-lane")],
            [Item("gate-progress"), Item("remote-lane"), Item("tick-completed")]
        ];
        var expectedGateReads = 1;
        foreach (var batch in batches)
        {
            await refresh.RefreshAsync(batch, scope: OwnerConsoleRefreshScope.None);
            Assert.Equal(1, fixture.State.Boards);
            Assert.Equal(1, fixture.Source.Reads);
            Assert.Equal(1, Calls(fixture, "board"));
            Assert.Equal(1, Calls(fixture, "activity"));
            Assert.Equal(1, Calls(fixture, "decisions"));
            Assert.Equal(++expectedGateReads, fixture.GateReads);
        }

        await refresh.RefreshAsync([Item("gate-progress"),
            Item("goal", "GOAL goal=11111111 state=Verified result=held reason=waiting")],
            scope: OwnerConsoleRefreshScope.None);
        Assert.Equal(2, fixture.State.Boards);
        Assert.Equal(2, fixture.Source.Reads);
        Assert.Equal(2, Calls(fixture, "activity"));
        Assert.Equal(2, Calls(fixture, "decisions"));
        Assert.Equal(++expectedGateReads, fixture.GateReads);

        // Periodic refreshes have no event batch and must still run all loaders.
        await refresh.RefreshAsync(skipIfBusy: true);
        Assert.Equal(3, fixture.State.Boards);
        Assert.Equal(3, Calls(fixture, "activity"));
        Assert.Equal(3, Calls(fixture, "decisions"));
    }

    [Theory]
    [InlineData("GATE-PROGRESS")]
    [InlineData("REMOTE-LANE")]
    [InlineData("EVIDENCE_START")]
    [InlineData("EVIDENCE_END")]
    [InlineData("goal")]
    [InlineData("landed")]
    public void IsPaneOnly_OtherKinds_KeepFullRoute(string kind) =>
        Assert.False(OwnerConsoleEventRoute.IsPaneOnly(new(DateTimeOffset.UnixEpoch, kind, "goal", "")));

    private static int Calls(OwnerConsoleRefreshReuseFixture fixture, string loader)
    {
        var match = Regex.Match(fixture.TimingLine!, @"\b" + loader + @"=(\d+)/");
        Assert.True(match.Success, $"No {loader} count in {fixture.TimingLine}");
        return int.Parse(match.Groups[1].Value);
    }
}
