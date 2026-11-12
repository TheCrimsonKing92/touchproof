using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: fixture-local stores, fake intake and an injected clock; no subprocesses.
public sealed class ConductorBoardFillFilingOrderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Newest_draft_files_first_unless_an_older_attempt_is_pending(bool pending)
    {
        using var h = new BoardFillFilingTestFixture();
        var older = h.Assessed();
        h.Drafts.Clock.UtcNow = h.Drafts.Clock.UtcNow.AddMinutes(1);
        var newer = h.Assessed();
        var attempt = pending ? h.Drafts.Store.BeginFiling(older.Id, h.Drafts.Clock.UtcNow) : null;
        var expected = pending ? older : newer;
        var untouched = pending ? newer : older;

        h.Host.ServiceTick(h.Drafts.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentFiling!, "selected draft filed");
        h.Host.ServiceTick(h.Drafts.Kernel);

        Assert.Equal(expected.Id, Assert.Single(h.Intake.Requests).DraftId);
        var stored = h.Drafts.Store.ReadAll();
        var result = Assert.Single(stored.Single(round => round.Id == expected.Id).Filings!);
        Assert.Equal("filed", result.Result);
        if (attempt is not null) Assert.Equal(attempt.Id, result.Id);
        Assert.Empty(stored.Single(round => round.Id == untouched.Id).Filings ?? []);
        Assert.Null(h.Host.CurrentFiling);
        Assert.Equal(1, h.Drafts.Store.FiledOnUtcDay(h.Drafts.Clock.UtcNow));
    }

    [Fact]
    public async Task Equal_start_times_choose_the_ordinally_first_id()
    {
        using var h = new BoardFillFilingTestFixture();
        var first = h.Assessed();
        var second = h.Assessed();
        Assert.Equal(first.StartedAt, second.StartedAt);
        var expected = new[] { first.Id, second.Id }.Order(StringComparer.Ordinal).First();

        h.Host.ServiceTick(h.Drafts.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentFiling!, "tie-broken draft filed");

        Assert.Equal(expected, Assert.Single(h.Intake.Requests).DraftId);
    }
}
