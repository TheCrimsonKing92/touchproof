using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: histories, clocks, drafting and file roots belong to each test.
public sealed class BoardFillConsecutiveNonDraftRoundsTests
{
    private static readonly DateTimeOffset Now = BoardFillReadyItemSelectorTests.Now;

    [Fact]
    public void Mixed_runs_ignore_held_and_unfinished_rounds_and_keep_items_separate()
    {
        var rounds = new[]
        {
            Round("stale"), Round("held"), Round("failed"), Round(null), Round("unknown"),
            Round("stale"), Round("failed", itemId: "b"), Round("failed", itemId: "b"),
            Round("failed"), Round("held")
        };

        var stopped = BoardFillConsecutiveNonDraftRounds.StoppedItems(rounds, Now);

        var entry = Assert.Single(stopped);
        Assert.Equal("a", entry.Key);
        Assert.Equal(4, entry.Value);
    }

    [Theory]
    [InlineData("failed,failed,draft,failed")]
    [InlineData("failed,failed,failed,draft")]
    [InlineData("failed,failed,held")]
    public void Draft_resets_the_run_and_two_non_drafts_do_not_stop(string outcomes)
    {
        var rounds = outcomes.Split(',').Select(outcome => Round(outcome));

        Assert.Empty(BoardFillConsecutiveNonDraftRounds.StoppedItems(rounds, Now));
    }

    [Fact]
    public void Chronological_order_overrides_the_supplied_order()
    {
        var rounds = new[]
        {
            Round("failed", minute: 4), Round("failed", minute: 3), Round("draft", minute: 2),
            Round("failed", minute: 1), Round("failed")
        };

        Assert.Empty(BoardFillConsecutiveNonDraftRounds.StoppedItems(rounds, Now));
        var stopped = BoardFillConsecutiveNonDraftRounds.StoppedItems(
            rounds.Prepend(Round("stale", minute: 5)), Now);
        Assert.Equal(3, Assert.Single(stopped).Value);
    }

    [Fact]
    public void Equal_start_times_preserve_supplied_order_for_the_draft_reset()
    {
        var failures = Enumerable.Range(0, 3).Select(_ => Round("failed")).ToArray();

        Assert.Empty(BoardFillConsecutiveNonDraftRounds.StoppedItems(
            failures.Append(Round("draft")), Now));
        Assert.Equal(3, Assert.Single(BoardFillConsecutiveNonDraftRounds.StoppedItems(
            failures.Prepend(Round("draft")), Now)).Value);
    }

    [Fact]
    public void Utc_day_uses_start_time_with_inclusive_midnight_and_exclusive_next_day()
    {
        var rounds = new[]
        {
            Round("failed") with { StartedAt = Now.AddTicks(-1), FinishedAt = Now.AddMinutes(1) },
            Round("failed"), Round("stale", minute: 1),
            Round("failed") with { StartedAt = Now.AddDays(1) }
        };
        var localNow = Now.AddHours(1).ToOffset(TimeSpan.FromHours(-6));
        Assert.Empty(BoardFillConsecutiveNonDraftRounds.StoppedItems(rounds, localNow));

        rounds = [.. rounds, Round("failed") with { StartedAt = Now.AddDays(1).AddTicks(-1) }];
        Assert.Equal(3, Assert.Single(BoardFillConsecutiveNonDraftRounds.StoppedItems(rounds, localNow)).Value);
        Assert.Empty(BoardFillConsecutiveNonDraftRounds.StoppedItems(rounds, Now.AddDays(1)));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public async Task Host_stops_one_item_reports_once_and_resumes_it_the_next_day(
        bool mixedOutcomes, int count)
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 10 };
        var first = BoardFillReadyItemSelectorTests.Item('a');
        var second = BoardFillReadyItemSelectorTests.Item('b');
        h.Items = [first, second];
        for (var index = 0; index < count; index++)
        {
            var round = h.Store.Begin(first, h.Clock.UtcNow);
            // Pre-model failures and stale rounds at a different head bypass existing exclusions.
            h.Store.Finish(round, new(mixedOutcomes && index % 2 == 0 ? "stale" : "failed",
                1, new string('d', 40), null, null, [], "fixture", FailureKind: BoardFillFailureKind.PreModel),
                h.Clock.UtcNow);
        }
        Assert.DoesNotContain(first.Id, h.Store.AlreadyDrafted(h.Items, h.MainHead));
        Assert.DoesNotContain(h.Store.ReadAll(), BoardFillFailureKind.CountsTowardItem);

        h.Host.ServiceTick(h.Kernel);
        Assert.NotNull(h.Host.CurrentRound);
        await PanelTestHarness.Signal(h.Host.CurrentRound, "other eligible item's draft finished");
        var running = Assert.Single(h.Store.ReadAll().Where(round => round.Outcome is null));
        Assert.Equal(second.Id, running.BacklogItemId);
        var line = Assert.Single(StopLines(h));
        Assert.Contains($"item={first.Id}", line);
        Assert.Contains($"consecutive={count}", line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);

        h.Host.ServiceTick(h.Kernel);
        h.Host.ServiceTick(h.Kernel);
        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(count + 1, h.Store.ReadAll().Count);
        Assert.Equal(count, h.Store.ReadAll().Count(round => round.BacklogItemId == first.Id));
        Assert.Single(StopLines(h));

        h.Clock.UtcNow = h.Clock.UtcNow.AddDays(1);
        h.Host.ServiceTick(h.Kernel);
        Assert.NotNull(h.Host.CurrentRound);
        await PanelTestHarness.Signal(h.Host.CurrentRound, "next-day resumed draft finished");
        Assert.Equal(first.Id, Assert.Single(h.Store.ReadAll().Where(round => round.Outcome is null)).BacklogItemId);
        Assert.Equal(count + 2, h.Store.ReadAll().Count);
    }

    [Fact]
    public async Task Host_uses_insertion_order_when_a_draft_resets_equal_time_failures()
    {
        using var h = new BoardFillTestHarness();
        var item = Assert.Single(h.Items);
        foreach (var outcome in new[] { "failed", "failed", "failed", "draft", "failed" })
        {
            var round = h.Store.Begin(item, h.Clock.UtcNow);
            h.Store.Finish(round, h.Draft() with { Kind = outcome, FailureKind = BoardFillFailureKind.PreModel }, h.Clock.UtcNow);
        }
        // The item edit releases AlreadyDrafted; only the daily run's draft reset permits selection.
        h.Items = [item with { UpdatedAt = item.UpdatedAt.AddMinutes(1) }];
        h.Host.ServiceTick(h.Kernel);

        Assert.NotNull(h.Host.CurrentRound);
        await PanelTestHarness.Signal(h.Host.CurrentRound, "draft after reset finished");
        Assert.Equal(item.Id, Assert.Single(h.Store.ReadAll().Where(round => round.Outcome is null)).BacklogItemId);
        Assert.Empty(StopLines(h));
    }

    [Fact]
    public void Next_day_keeps_the_existing_unchanged_main_stale_exclusion()
    {
        using var h = new BoardFillTestHarness();
        var item = Assert.Single(h.Items);
        for (var index = 0; index < 3; index++)
        {
            var round = h.Store.Begin(item, h.Clock.UtcNow);
            h.Store.Finish(round, h.Draft() with { Kind = "stale" }, h.Clock.UtcNow);
        }
        h.Host.ServiceTick(h.Kernel);
        Assert.Null(h.Host.CurrentRound);
        Assert.Single(StopLines(h));

        h.Clock.UtcNow = h.Clock.UtcNow.AddDays(1);
        h.Host.ServiceTick(h.Kernel);

        Assert.Null(h.Host.CurrentRound);
        Assert.Equal(3, h.Store.ReadAll().Count);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public void Host_reports_a_new_stop_for_the_same_item_on_the_next_utc_day()
    {
        using var h = new BoardFillTestHarness();
        var item = Assert.Single(h.Items);
        for (var day = 0; day < 2; day++)
        {
            for (var index = 0; index < 3; index++)
            {
                var round = h.Store.Begin(item, h.Clock.UtcNow);
                h.Store.Finish(round, new("failed", 1, null, null, null, [], "fixture",
                    FailureKind: BoardFillFailureKind.PreModel), h.Clock.UtcNow);
            }
            h.Host.ServiceTick(h.Kernel);
            Assert.Null(h.Host.CurrentRound);
            Assert.Equal(day + 1, StopLines(h).Length);
            h.Clock.UtcNow = h.Clock.UtcNow.AddDays(1);
        }
    }

    private static BoardFillDraftRound Round(string? outcome, int minute = 0, string itemId = "a") =>
        new(Guid.NewGuid().ToString("N"), itemId, Now, null, null, outcome, null, null, [],
            Now.AddMinutes(minute), Now.AddMinutes(minute));

    private static string[] StopLines(BoardFillTestHarness h) => h.Events().Select(line =>
    {
        using var entry = BoardFillTestHarness.Event(line);
        return entry.RootElement.GetProperty("detail").GetString()!;
    }).Where(detail => detail.StartsWith("BOARD_FILL_ITEM_STOPPED", StringComparison.Ordinal)).ToArray();
}
