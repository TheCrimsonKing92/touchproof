using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each harness owns its SQLite and event log, with an injected clock.
public sealed class ConductorBoardFillHostFailureBreakerTests
{
    [Theory]
    [InlineData(ConductorBoardFillMode.File)]
    [InlineData(ConductorBoardFillMode.Shadow)]
    public async Task Backoff_reports_once_survives_restart_and_resumes_at_retry(ConductorBoardFillMode mode)
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMode = mode, BoardFillMaxDraftsPerDay = null };
        SeedFailures(h);
        var retry = h.Clock.UtcNow.AddMinutes(5);
        h.Host.ServiceTick(h.Kernel);
        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(0, h.Calls);
        var line = Assert.Single(BackoffLines(h));
        using (var entry = BoardFillTestHarness.Event(line))
        {
            Assert.Equal("board-fill-draft", entry.RootElement.GetProperty("eventKind").GetString());
            var detail = entry.RootElement.GetProperty("detail").GetString()!;
            Assert.Contains("consecutive=3", detail);
            Assert.Contains("delay=300s", detail);
            Assert.Contains($"retry={retry:O}", detail);
            var latest = h.Store.ReadAll(newestFirst: true).First();
            Assert.Contains("round=" + latest.Id, detail);
        }
        h.Host.ServiceTick(h.Kernel);
        Assert.Single(BackoffLines(h));
        h.Host.Stop();
        var restarted = h.NewHost();
        try
        {
            restarted.ServiceTick(h.Kernel);
            Assert.Null(restarted.CurrentRound);
            Assert.Single(BackoffLines(h));
            h.Clock.UtcNow = retry;
            restarted.ServiceTick(h.Kernel);
            Assert.NotNull(restarted.CurrentRound);
            await PanelTestHarness.Signal(restarted.CurrentRound, "draft resumed at retry time");
            Assert.Equal(1, h.Calls);
            Assert.Single(BackoffLines(h));
        }
        finally { restarted.Stop(); }
    }

    [Fact]
    public void Off_does_not_report_and_an_expired_period_has_no_historical_event()
    {
        using var h = new BoardFillTestHarness();
        SeedFailures(h);
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Off };
        h.Host.ServiceTick(h.Kernel);
        Assert.Empty(h.Events());
        Assert.Null(h.Host.CurrentRound);
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(5);
        h.Host.ServiceTick(h.Kernel);
        Assert.NotNull(h.Host.CurrentRound);
        Assert.Empty(BackoffLines(h));
    }

    [Fact]
    public void A_further_failure_establishes_a_new_period_and_event()
    {
        using var h = new BoardFillTestHarness();
        SeedFailures(h);
        h.Host.ServiceTick(h.Kernel);
        Assert.Single(BackoffLines(h));
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(1);
        var round = h.Store.Begin(BoardFillReadyItemSelectorTests.Item('e'), h.Clock.UtcNow);
        h.Store.Finish(round, new("failed", 1, null, null, null, []), h.Clock.UtcNow);
        h.Host.ServiceTick(h.Kernel);
        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(2, BackoffLines(h).Length);
        var line = BackoffLines(h)[^1];
        Assert.Contains("consecutive=4", line);
        Assert.Contains("delay=600s", line);
        h.Host.ServiceTick(h.Kernel);
        Assert.Equal(2, BackoffLines(h).Length);
    }

    private static void SeedFailures(BoardFillTestHarness h)
    {
        foreach (var key in new[] { 'b', 'c', 'd' })
        {
            var round = h.Store.Begin(BoardFillReadyItemSelectorTests.Item(key), h.Clock.UtcNow);
            h.Store.Finish(round, new(key == 'c' ? "stale" : "failed", 1, null, null, null, []), h.Clock.UtcNow);
        }
    }

    private static string[] BackoffLines(BoardFillTestHarness h) =>
        h.Events().Where(line => line.Contains("BOARD_FILL_BACKOFF", StringComparison.Ordinal)).ToArray();
}
