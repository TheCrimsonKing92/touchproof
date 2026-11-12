using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each harness owns its files and SQLite store; time and drafting are injected.
public sealed class BoardFillDailyAllowanceTests
{
    [Fact]
    public async Task Backlog_changed_refusal_releases_a_round_for_the_next_tick()
    {
        using var h = new BoardFillTestHarness();
        var draft = await ReachDailyLimit(h);
        var attempt = h.Store.BeginFiling(draft.Id, h.Clock.UtcNow);
        h.Store.FinishFiling(draft.Id, attempt with { Result = "refused", Terminal = true,
            Reason = "not-fileable:backlog-changed", FinishedAt = h.Clock.UtcNow });
        Assert.Equal(0, h.Store.StartedOnUtcDay(h.Clock.UtcNow));

        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "replacement draft finished");

        Assert.Equal(2, h.Calls);
        var next = Assert.Single(h.Store.ReadAll().Where(round => round.Outcome is null));
        Assert.NotEqual(draft.BacklogItemId, next.BacklogItemId);
        Assert.Equal(2, h.Store.ReadAll().Count);
        Assert.Equal(1, h.Store.StartedOnUtcDay(h.Clock.UtcNow));
    }

    [Fact]
    public async Task Unreleased_round_leaves_the_next_tick_at_the_daily_limit()
    {
        using var h = new BoardFillTestHarness();
        await ReachDailyLimit(h);

        h.Host.ServiceTick(h.Kernel);

        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(1, h.Calls);
        Assert.Single(h.Store.ReadAll());
        Assert.Equal(1, h.Store.StartedOnUtcDay(h.Clock.UtcNow));
    }

    [Theory]
    [InlineData("draft", "stale-head", true)]
    [InlineData("draft", "not-fileable:backlog-linked", true)]
    [InlineData("draft", "not-fileable:owner-gated", true)]
    [InlineData("draft", "not-fileable:backlog-changed,owner-gated", true)]
    [InlineData("draft", "NOT-FILEABLE:BACKLOG-CHANGED", true)]
    [InlineData("draft", "not-fileable:backlog-changed", false)]
    [InlineData("failed", null, false)]
    [InlineData("stale", null, false)]
    public async Task Other_outcomes_keep_the_next_tick_at_the_limit(string outcome, string? reason, bool terminal)
    {
        using var h = new BoardFillTestHarness();
        h.Reply = () => h.Draft() with { Kind = outcome };
        var draft = await ReachDailyLimit(h);
        if (reason is not null)
        {
            var attempt = h.Store.BeginFiling(draft.Id, h.Clock.UtcNow);
            h.Store.FinishFiling(draft.Id, attempt with { Result = "refused", Terminal = terminal,
                Reason = reason, FinishedAt = h.Clock.UtcNow });
        }

        h.Host.ServiceTick(h.Kernel);

        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(1, h.Calls);
        Assert.Single(h.Store.ReadAll());
        Assert.Equal(1, h.Store.StartedOnUtcDay(h.Clock.UtcNow));
    }

    [Fact]
    public void Held_round_and_a_different_utc_day_do_not_count()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var round = h.Finished();
        Assert.Equal(1, BoardFillDailyAllowance.Count([round], h.Clock.UtcNow.ToOffset(TimeSpan.FromHours(-6))));
        Assert.Equal(0, BoardFillDailyAllowance.Count([round with { Outcome = "held" }], h.Clock.UtcNow));
        Assert.Equal(0, BoardFillDailyAllowance.Count([round], h.Clock.UtcNow.AddDays(1)));
    }

    [Fact]
    public void Any_matching_terminal_attempt_releases_the_round_regardless_of_attempt_day()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var round = h.Finished();
        var tomorrow = h.Clock.UtcNow.AddDays(1);
        var first = h.Store.BeginFiling(round.Id, tomorrow);
        h.Store.FinishFiling(round.Id, first with { Result = "refused", Terminal = true,
            Reason = "not-fileable:backlog-changed", FinishedAt = tomorrow });
        var later = h.Store.BeginFiling(round.Id, tomorrow.AddMinutes(1));
        h.Store.FinishFiling(round.Id, later with { Result = "refused", Terminal = true,
            Reason = "not-fileable:owner-gated", FinishedAt = tomorrow.AddMinutes(1) });

        Assert.Equal(0, h.Store.StartedOnUtcDay(h.Clock.UtcNow));
        var stored = Assert.Single(h.Store.ReadAll());
        Assert.Equal(2, stored.Filings!.Count);
        Assert.Equal(1, BoardFillDailyAllowance.Count([stored with { Filings = [later] }], h.Clock.UtcNow));
    }

    private static async Task<BoardFillDraftRound> ReachDailyLimit(BoardFillTestHarness h)
    {
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 1 };
        h.Items = [BoardFillReadyItemSelectorTests.Item('a'), BoardFillReadyItemSelectorTests.Item('b')];
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Host.CurrentRound!, "daily allowance draft finished");
        h.Host.ServiceTick(h.Kernel);
        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(1, h.Calls);
        Assert.Equal(1, h.Store.StartedOnUtcDay(h.Clock.UtcNow));
        return Assert.Single(h.Store.ReadAll());
    }
}
