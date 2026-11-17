using System.Text.Json;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each harness owns its SQLite/event root, clock and model seams.
public sealed class ConductorBoardFillHostBelowTargetTests
{
    [Theory]
    [InlineData("mode-not-file")]
    [InlineData("daily-cap-reached")]
    [InlineData("author-refusal-streak")]
    [InlineData("no-ready-item")]
    public void Blocked_reason_emits_exactly_once_on_third_tick(string reason)
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with
        {
            BoardFillMode = reason == "mode-not-file" ? ConductorBoardFillMode.Shadow : ConductorBoardFillMode.File,
            BoardFillMaxDraftsPerDay = reason is "mode-not-file" or "daily-cap-reached" ? 0 : 10,
            BoardFillTargetActiveGoals = 2
        };
        h.Kernel.CreateGoal("Active fixture");
        var terminal = h.Kernel.CreateGoal("Terminal fixture");
        h.Kernel.CancelGoal(terminal.Id, "Fixture");
        if (reason == "no-ready-item") h.Items = [];
        if (reason == "author-refusal-streak")
        {
            for (var roundIndex = 0; roundIndex < 3; roundIndex++)
            {
                var round = h.Store.Begin(h.Items[0], h.Clock.UtcNow);
                h.Store.Finish(round, new("failed", 1, null, null, null, [], "fixture refusal"), h.Clock.UtcNow);
            }
        }

        var thirdTick = h.Clock.UtcNow.AddMinutes(3);
        for (var tick = 1; tick <= 4; tick++)
        {
            h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(1);
            h.Host.ServiceTick(h.Kernel);
            var observations = Observations(h);
            if (tick < 3) Assert.Empty(observations);
            else AssertEvent(Assert.Single(observations),
                $"BOARD_BELOW_TARGET count=1 target=2 reason={reason}", "decision", thirdTick);
            Assert.Null(h.Host.CurrentRound);
        }
        Assert.Equal(0, h.Calls);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void At_target_stays_silent_then_below_target_needs_three_blocked_ticks(int active, int target)
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillTargetActiveGoals = target, BoardFillMaxDraftsPerDay = 0 };
        for (var i = 0; i < active; i++) h.Kernel.CreateGoal("Active fixture " + i);
        for (var tick = 0; tick < 12; tick++) h.Host.ServiceTick(h.Kernel);
        Assert.Empty(Observations(h));
        Assert.Equal(0, h.Calls);

        h.Policy = h.Policy with { BoardFillTargetActiveGoals = active + 1 };
        h.Host.ServiceTick(h.Kernel);
        h.Host.ServiceTick(h.Kernel);
        Assert.Empty(Observations(h));
        h.Host.ServiceTick(h.Kernel);
        AssertEvent(Assert.Single(Observations(h)),
            $"BOARD_BELOW_TARGET count={active} target={active + 1} reason=mode-not-file", "decision", h.Clock.UtcNow);
    }

    [Fact]
    public void Reason_change_recovery_and_fresh_streak_append_exact_transitions()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillTargetActiveGoals = 1, BoardFillMaxDraftsPerDay = 0 };
        h.Host.ServiceTick(h.Kernel);
        h.Host.ServiceTick(h.Kernel);
        Assert.Empty(Observations(h));
        h.Host.ServiceTick(h.Kernel);
        AssertEvent(Assert.Single(Observations(h)),
            "BOARD_BELOW_TARGET count=0 target=1 reason=mode-not-file", "decision", h.Clock.UtcNow);

        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.File };
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(1);
        h.Host.ServiceTick(h.Kernel);
        var observations = Observations(h);
        Assert.Equal(2, observations.Length);
        AssertEvent(observations[1], "BOARD_BELOW_TARGET count=0 target=1 reason=daily-cap-reached",
            "decision", h.Clock.UtcNow);
        h.Host.ServiceTick(h.Kernel);
        Assert.Equal(2, Observations(h).Length);

        var goal = h.Kernel.CreateGoal("Restored board");
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(1);
        h.Host.ServiceTick(h.Kernel);
        observations = Observations(h);
        Assert.Equal(3, observations.Length);
        AssertEvent(observations[2], "BOARD_TARGET_RECOVERED count=1 target=1", "outcome", h.Clock.UtcNow);
        h.Host.ServiceTick(h.Kernel);
        Assert.Equal(3, Observations(h).Length);

        h.Kernel.CancelGoal(goal.Id, "Start a new thin-board episode");
        h.Host.ServiceTick(h.Kernel);
        h.Host.ServiceTick(h.Kernel);
        Assert.Equal(3, Observations(h).Length);
        h.Host.ServiceTick(h.Kernel);
        observations = Observations(h);
        Assert.Equal(4, observations.Length);
        AssertEvent(observations[3], "BOARD_BELOW_TARGET count=0 target=1 reason=daily-cap-reached",
            "decision", h.Clock.UtcNow);
    }

    [Fact]
    public void Off_ticks_neither_advance_nor_clear_monitor_state()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with
        {
            BoardFillMode = ConductorBoardFillMode.Off,
            BoardFillMaxDraftsPerDay = 0,
            BoardFillTargetActiveGoals = 1
        };
        for (var tick = 0; tick < 5; tick++) h.Host.ServiceTick(h.Kernel);
        Assert.Empty(h.Events());
        Assert.False(File.Exists(h.StorePath));

        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        h.Host.ServiceTick(h.Kernel);
        h.Host.ServiceTick(h.Kernel);
        Assert.Empty(Observations(h));
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Off };
        for (var tick = 0; tick < 5; tick++) h.Host.ServiceTick(h.Kernel);
        Assert.Empty(Observations(h));
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        h.Host.ServiceTick(h.Kernel);
        AssertEvent(Assert.Single(Observations(h)),
            "BOARD_BELOW_TARGET count=0 target=1 reason=mode-not-file", "decision", h.Clock.UtcNow);

        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Off };
        h.Kernel.CreateGoal("Recovery while Off");
        h.Host.ServiceTick(h.Kernel);
        Assert.Single(Observations(h));
        h.Policy = h.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        h.Host.ServiceTick(h.Kernel);
        var observations = Observations(h);
        Assert.Equal(2, observations.Length);
        AssertEvent(observations[1], "BOARD_TARGET_RECOVERED count=1 target=1", "outcome", h.Clock.UtcNow);
    }

    [Fact]
    public async Task In_flight_round_resets_streak_even_when_daily_cap_is_reached()
    {
        using var h = new BoardFillTestHarness();
        h.Held = true;
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 0 };
        h.Host.ServiceTick(h.Kernel);
        h.Host.ServiceTick(h.Kernel);
        Assert.Empty(Observations(h));

        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 10 };
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Started.Task, "draft entered held model seam");
        var round = h.Host.CurrentRound;
        Assert.NotNull(round);
        Assert.False(round.IsCompleted);
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 0 };
        for (var tick = 0; tick < 4; tick++) h.Host.ServiceTick(h.Kernel);
        Assert.Same(round, h.Host.CurrentRound);
        Assert.Empty(Observations(h));

        h.Release();
        await PanelTestHarness.Signal(round, "held draft finished");
        h.Host.ServiceTick(h.Kernel);
        h.Host.ServiceTick(h.Kernel);
        Assert.Empty(Observations(h));
        h.Host.ServiceTick(h.Kernel);
        AssertEvent(Assert.Single(Observations(h)),
            "BOARD_BELOW_TARGET count=0 target=10 reason=mode-not-file", "decision", h.Clock.UtcNow);
    }

    [Fact]
    public async Task Reaching_target_recovers_an_alert_while_round_is_in_flight()
    {
        using var h = new BoardFillTestHarness();
        h.Held = true;
        h.Policy = h.Policy with { BoardFillTargetActiveGoals = 1, BoardFillMaxDraftsPerDay = 0 };
        for (var tick = 0; tick < 3; tick++) h.Host.ServiceTick(h.Kernel);
        AssertEvent(Assert.Single(Observations(h)),
            "BOARD_BELOW_TARGET count=0 target=1 reason=mode-not-file", "decision", h.Clock.UtcNow);

        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 10 };
        h.Host.ServiceTick(h.Kernel);
        await PanelTestHarness.Signal(h.Started.Task, "draft entered held model seam");
        var round = h.Host.CurrentRound;
        Assert.NotNull(round);
        Assert.False(round.IsCompleted);
        h.Kernel.CreateGoal("Restored board during drafting");
        h.Clock.UtcNow = h.Clock.UtcNow.AddMinutes(1);
        h.Host.ServiceTick(h.Kernel);
        Assert.Same(round, h.Host.CurrentRound);
        var observations = Observations(h);
        Assert.Equal(2, observations.Length);
        AssertEvent(observations[1], "BOARD_TARGET_RECOVERED count=1 target=1", "outcome", h.Clock.UtcNow);
        h.Host.ServiceTick(h.Kernel);
        Assert.Equal(2, Observations(h).Length);
    }

    [Fact]
    public void New_host_starts_a_fresh_streak_instead_of_loading_alert_state()
    {
        using var h = new BoardFillTestHarness();
        h.Policy = h.Policy with { BoardFillMaxDraftsPerDay = 0 };
        for (var tick = 0; tick < 3; tick++) h.Host.ServiceTick(h.Kernel);
        Assert.Single(Observations(h));
        var restarted = h.NewHost();
        try
        {
            restarted.ServiceTick(h.Kernel);
            restarted.ServiceTick(h.Kernel);
            Assert.Single(Observations(h));
            restarted.ServiceTick(h.Kernel);
            Assert.Equal(2, Observations(h).Length);
        }
        finally { restarted.Stop(); }
    }

    private static JsonElement[] Observations(BoardFillTestHarness h) => h.Events().Select(line =>
    {
        using var entry = BoardFillTestHarness.Event(line);
        return entry.RootElement.Clone();
    }).Where(entry => entry.GetProperty("detail").GetString() is { } detail &&
        (detail.StartsWith("BOARD_BELOW_TARGET ", StringComparison.Ordinal) ||
         detail.StartsWith("BOARD_TARGET_RECOVERED ", StringComparison.Ordinal))).ToArray();

    private static void AssertEvent(JsonElement entry, string detail, string classification, DateTimeOffset timestamp)
    {
        Assert.Equal(detail, entry.GetProperty("detail").GetString());
        Assert.Equal("board-fill-draft", entry.GetProperty("eventKind").GetString());
        Assert.Equal(classification, entry.GetProperty("operator").GetString());
        Assert.Equal(timestamp, entry.GetProperty("timestamp").GetDateTimeOffset());
    }
}
