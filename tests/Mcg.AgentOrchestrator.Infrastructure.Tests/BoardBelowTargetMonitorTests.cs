using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns an in-memory monitor and supplies all observations.
public sealed class BoardBelowTargetMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("mode-not-file")]
    [InlineData("daily-cap-reached")]
    [InlineData("author-refusal-streak")]
    [InlineData("no-ready-item")]
    public void Blocked_streak_emits_on_third_tick_and_suppresses_same_reason(string reason)
    {
        var monitor = new BoardBelowTargetMonitor();
        Assert.Null(monitor.Observe(3, 9, reason, Now));
        Assert.Null(monitor.Observe(3, 9, reason, Now));
        Assert.Equal($"BOARD_BELOW_TARGET count=3 target=9 reason={reason}",
            monitor.Observe(3, 9, reason, Now));
        Assert.Null(monitor.Observe(3, 9, reason, Now));
    }

    [Theory]
    [InlineData(9, 9)]
    [InlineData(10, 9)]
    [InlineData(0, 0)]
    public void At_target_without_alert_stays_silent(int count, int target)
    {
        var monitor = new BoardBelowTargetMonitor();
        for (var tick = 0; tick < 12; tick++)
            Assert.Null(monitor.Observe(count, target, "daily-cap-reached", Now));
    }

    [Fact]
    public void Changing_reason_before_threshold_counts_the_same_streak()
    {
        var monitor = new BoardBelowTargetMonitor();
        Assert.Null(monitor.Observe(3, 9, "mode-not-file", Now));
        Assert.Null(monitor.Observe(3, 9, "daily-cap-reached", Now));
        Assert.Equal("BOARD_BELOW_TARGET count=3 target=9 reason=no-ready-item",
            monitor.Observe(3, 9, "no-ready-item", Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unblocked_or_at_target_tick_resets_an_unalerted_streak(bool atTarget)
    {
        var monitor = new BoardBelowTargetMonitor();
        Assert.Null(monitor.Observe(3, 9, "daily-cap-reached", Now));
        Assert.Null(monitor.Observe(3, 9, "daily-cap-reached", Now));
        Assert.Null(monitor.Observe(atTarget ? 9 : 3, 9,
            atTarget ? "daily-cap-reached" : null, Now));
        Assert.Null(monitor.Observe(3, 9, "daily-cap-reached", Now));
        Assert.Null(monitor.Observe(3, 9, "daily-cap-reached", Now));
        Assert.Equal("BOARD_BELOW_TARGET count=3 target=9 reason=daily-cap-reached",
            monitor.Observe(3, 9, "daily-cap-reached", Now));
    }

    [Fact]
    public void Alerted_reason_changes_and_reversions_emit_immediately()
    {
        var monitor = Alerted();
        Assert.Equal("BOARD_BELOW_TARGET count=3 target=9 reason=no-ready-item",
            monitor.Observe(3, 9, "no-ready-item", Now));
        Assert.Null(monitor.Observe(3, 9, "no-ready-item", Now));
        Assert.Equal("BOARD_BELOW_TARGET count=3 target=9 reason=daily-cap-reached",
            monitor.Observe(3, 9, "daily-cap-reached", Now));
    }

    [Fact]
    public void Unblocked_gap_preserves_alert_and_last_emitted_reason()
    {
        var monitor = Alerted();
        Assert.Null(monitor.Observe(3, 9, null, Now));
        Assert.Null(monitor.Observe(3, 9, "daily-cap-reached", Now));
        Assert.Null(monitor.Observe(3, 9, null, Now));
        Assert.Equal("BOARD_BELOW_TARGET count=3 target=9 reason=no-ready-item",
            monitor.Observe(3, 9, "no-ready-item", Now));
        Assert.Null(monitor.Observe(3, 9, null, Now));
        Assert.Equal("BOARD_TARGET_RECOVERED count=9 target=9",
            monitor.Observe(9, 9, null, Now));
    }

    [Theory]
    [InlineData(9, 9)]
    [InlineData(10, 9)]
    [InlineData(3, 3)]
    public void Recovery_emits_once_and_next_episode_needs_three_ticks(int count, int target)
    {
        var monitor = Alerted();
        Assert.Equal($"BOARD_TARGET_RECOVERED count={count} target={target}",
            monitor.Observe(count, target, null, Now));
        Assert.Null(monitor.Observe(count, target, "daily-cap-reached", Now));
        Assert.Null(monitor.Observe(3, 9, "daily-cap-reached", Now));
        Assert.Null(monitor.Observe(3, 9, "daily-cap-reached", Now));
        Assert.Equal("BOARD_BELOW_TARGET count=3 target=9 reason=daily-cap-reached",
            monitor.Observe(3, 9, "daily-cap-reached", Now));
    }

    [Fact]
    public void Count_and_target_changes_below_target_preserve_streak_and_suppression()
    {
        var monitor = new BoardBelowTargetMonitor();
        Assert.Null(monitor.Observe(1, 9, "daily-cap-reached", Now));
        Assert.Null(monitor.Observe(2, 8, "daily-cap-reached", Now));
        Assert.Equal("BOARD_BELOW_TARGET count=3 target=7 reason=daily-cap-reached",
            monitor.Observe(3, 7, "daily-cap-reached", Now));
        Assert.Null(monitor.Observe(4, 6, "daily-cap-reached", Now));
    }

    private static BoardBelowTargetMonitor Alerted()
    {
        var monitor = new BoardBelowTargetMonitor();
        Assert.Null(monitor.Observe(3, 9, "daily-cap-reached", Now));
        Assert.Null(monitor.Observe(3, 9, "daily-cap-reached", Now));
        Assert.Equal("BOARD_BELOW_TARGET count=3 target=9 reason=daily-cap-reached",
            monitor.Observe(3, 9, "daily-cap-reached", Now));
        return monitor;
    }
}
