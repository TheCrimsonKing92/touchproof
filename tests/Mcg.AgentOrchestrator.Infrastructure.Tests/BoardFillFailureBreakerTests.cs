using Mcg.AgentOrchestrator.App.Orchestration;

// Pure histories and a fixed clock; no shared state or external processes.
public sealed class BoardFillFailureBreakerTests
{
    private static readonly DateTimeOffset Now = BoardFillReadyItemSelectorTests.Now;

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(5, 20)]
    [InlineData(6, 40)]
    [InlineData(7, 60)]
    [InlineData(64, 60)]
    public void Cross_item_streak_grows_to_a_capped_delay(int count, int minutes)
    {
        var rounds = Enumerable.Range(0, count)
            .Select(i => Round(i % 2 == 0 ? "stale" : "failed", i.ToString())).ToArray();
        var result = BoardFillFailureBreaker.Evaluate(rounds, Now);
        if (minutes == 0) Assert.Null(result);
        else
        {
            Assert.NotNull(result);
            Assert.Equal(count, result.ConsecutiveCount);
            Assert.Equal(TimeSpan.FromMinutes(minutes), result.Delay);
            Assert.Equal(Now.AddMinutes(minutes), result.RetryAt);
            Assert.Equal(rounds[^1].Id, result.RoundId);
            Assert.Null(BoardFillFailureBreaker.Evaluate(rounds, result.RetryAt));
        }
    }

    [Fact]
    public void Draft_resets_count_and_escalation_but_expiry_and_midnight_do_not()
    {
        var rounds = Enumerable.Range(0, 8).Select(_ => Round("failed")).ToList();
        Assert.Null(BoardFillFailureBreaker.Evaluate(rounds, Now.AddDays(1)));
        rounds.Add(Round("stale") with { FinishedAt = Now.AddDays(1) });
        var continued = BoardFillFailureBreaker.Evaluate(rounds, Now.AddDays(1));
        Assert.NotNull(continued);
        Assert.Equal(9, continued.ConsecutiveCount);
        Assert.Equal(TimeSpan.FromMinutes(60), continued.Delay);
        rounds.Add(Round("draft") with { FinishedAt = Now.AddDays(1) });
        Assert.Null(BoardFillFailureBreaker.Evaluate(rounds, Now.AddDays(1)));
        rounds.AddRange(Enumerable.Range(0, 3).Select(_ =>
            Round("failed") with { FinishedAt = Now.AddDays(1) }));
        var reset = BoardFillFailureBreaker.Evaluate(rounds, Now.AddDays(1));
        Assert.NotNull(reset);
        Assert.Equal(3, reset.ConsecutiveCount);
        Assert.Equal(TimeSpan.FromMinutes(5), reset.Delay);
    }

    [Fact]
    public void Held_unknown_and_unfinished_rounds_neither_count_nor_reset()
    {
        var rounds = new[] { Round("failed"), Round("held"), Round("stale"),
            Round("draft") with { FinishedAt = null }, Round("failed") with { FinishedAt = null },
            Round(null), Round("unknown"), Round("failed") };
        Assert.Equal(3, BoardFillFailureBreaker.Evaluate(rounds, Now)!.ConsecutiveCount);
    }

    [Fact]
    public void Completion_order_overrides_start_order_and_equal_times_keep_input_order()
    {
        var failures = Enumerable.Range(0, 3).Select(_ => Round("failed")).ToArray();
        var draft = Round("draft");
        Assert.Null(BoardFillFailureBreaker.Evaluate(failures.Append(draft), Now));
        Assert.NotNull(BoardFillFailureBreaker.Evaluate(failures.Prepend(draft), Now));
        // A draft supplied last still precedes failures when it completed earlier,
        // even though its start time is later than theirs.
        Assert.NotNull(BoardFillFailureBreaker.Evaluate(failures.Append(
            draft with { StartedAt = Now, FinishedAt = Now.AddTicks(-1) }), Now));
    }

    private static BoardFillDraftRound Round(string? outcome, string item = "a") =>
        new(Guid.NewGuid().ToString("N"), item, Now, null, null, outcome, null, null, [],
            Now.AddMinutes(-1), outcome is null ? null : Now);
}
