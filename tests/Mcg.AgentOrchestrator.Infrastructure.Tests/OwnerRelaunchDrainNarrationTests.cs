using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: only in-memory events and recorded timestamps are used.
public sealed class OwnerRelaunchDrainNarrationTests
{
    private const string Goal = "11111111";
    private const string Other = "22222222";

    public static TheoryData<string, string, string?> TerminalCases => new()
    {
        { "loop-relaunch", "LOOP_RELAUNCH_REBUILD", Goal },
        { "loop-handoff", "LOOP_HANDOFF", Goal },
        { "loop-handoff", "LOOP_HANDOFF", null },
        { "loop-handoff", "LOOP_HANDOFF_FAILED reason=publish_failed", null },
        { "loop-handoff", "ACTIVATION_ADOPTED", null },
        { "loop-handoff", "ACTIVATION_REVERTED reason=startup_failed", null },
        { "loop-handoff", "ACTIVATION_FAILED_BOTH reason=startup_failed", null },
        { "loop-stop", "LOOP_STOP reason=self-relaunch-handoff", null }
    };

    [Fact]
    public void Narrate_ScheduledThenRepeatedDrains_YieldsOneItemWithLatestCountsReasonAndElapsed()
    {
        var scheduled = Scheduled(0);
        var drains = new[] { Drain(6), Drain(12), Drain(363) };

        var items = Narrate([scheduled, ..drains]);

        var item = Assert.Single(DrainItems(items));
        Assert.Contains("2 running workers", item.Phrase);
        Assert.Contains("0 live gates", item.Phrase);
        Assert.Contains("reason: dispatch-handling-changed", item.Phrase);
        Assert.Contains("elapsed " + ConsoleDurationFormat.Format(drains[^1].Timestamp - scheduled.Timestamp), item.Phrase);
        Assert.Equal(drains[^1].Timestamp, item.Timestamp);
        Assert.Equal(new[] { scheduled }.Concat(drains), item.Evidence);
        Assert.Single(items, value => value.Phrase.StartsWith("Landed:"));
        Assert.Equal(2, items.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Narrate_DrainWithoutEarlierScheduledEvent_YieldsNoDrainItem(bool otherGoalScheduled)
    {
        var events = new List<OwnerConductEvent>();
        if (otherGoalScheduled) events.Add(Scheduled(0, Other));
        events.Add(Drain(6));
        events.Add(Scheduled(12));

        Assert.Empty(DrainItems(Narrate(events)));
    }

    [Theory]
    [MemberData(nameof(TerminalCases))]
    public void Narrate_DrainAfterRelaunchTerminalEvent_YieldsNoDrainItem(string kind, string detail, string? goal)
    {
        var events = new[] { Scheduled(0), Event(3, kind, detail, goal), Drain(6) };

        Assert.Empty(DrainItems(Narrate(events)));
    }

    [Theory]
    [MemberData(nameof(TerminalCases))]
    public void Narrate_TerminalRetainsEarlierDrainAndIgnoresLaterDrain(string kind, string detail, string? goal)
    {
        var scheduled = Scheduled(0);
        var before = Drain(6);
        var after = Drain(20, "active=9 liveGates=9");

        var item = Assert.Single(DrainItems(Narrate([scheduled, before, Event(12, kind, detail, goal), after])));

        Assert.Equal(before.Timestamp, item.Timestamp);
        Assert.Equal(new[] { scheduled, before }, item.Evidence);
        Assert.DoesNotContain(after, item.Evidence!);
        Assert.Contains("2 running workers", item.Phrase);
    }

    [Theory]
    [InlineData("loop-relaunch", "LOOP_RELAUNCH_REBUILD")]
    [InlineData("loop-handoff", "LOOP_HANDOFF")]
    [InlineData("loop-handoff", "ACTIVATION_ADOPTED")]
    [InlineData("loop-handoff", "ACTIVATION_REVERTED")]
    [InlineData("loop-handoff", "LOOP_HANDOFF_PENDING")]
    public void Narrate_UnrelatedTerminalDoesNotCloseGoalWindow(string kind, string detail)
    {
        var drain = Drain(12);

        var item = Assert.Single(DrainItems(Narrate([Scheduled(0), Event(6, kind, detail, Other), drain])));

        Assert.Equal(drain.Timestamp, item.Timestamp);
        Assert.Contains("elapsed " + ConsoleDurationFormat.Format(TimeSpan.FromSeconds(12)), item.Phrase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("reason=identity-unproven")]
    public void Narrate_LatestSampleReplacesCountsAndOptionalReason(string reason)
    {
        var last = Drain(12, "extra=ignored liveGates=1 " + reason + " active=1");

        var item = Assert.Single(DrainItems(Narrate([Scheduled(0), Drain(6), last])));

        Assert.Contains("1 running worker and 1 live gate", item.Phrase);
        Assert.DoesNotContain("dispatch-handling-changed", item.Phrase);
        Assert.Equal(last.Timestamp, item.Timestamp);
        if (reason.Length == 0) Assert.DoesNotContain("reason:", item.Phrase);
        else Assert.Contains("reason: identity-unproven", item.Phrase);
    }

    [Theory]
    [InlineData("active=-1 liveGates=0")]
    [InlineData("active=2 liveGates=-1")]
    [InlineData("active=no liveGates=0")]
    [InlineData("active=2 liveGates=no")]
    [InlineData("active=2147483648 liveGates=0")]
    [InlineData("active=2")]
    [InlineData("liveGates=0")]
    public void Narrate_InvalidSampleDoesNotCreateOrReplaceDrainItem(string fields)
    {
        var scheduled = Scheduled(0);
        var valid = Drain(6);
        var invalid = Drain(12, fields);

        Assert.Empty(DrainItems(Narrate([scheduled, invalid])));
        var item = Assert.Single(DrainItems(Narrate([scheduled, valid, invalid])));
        Assert.Equal(valid.Timestamp, item.Timestamp);
        Assert.Equal(new[] { scheduled, valid }, item.Evidence);
    }

    [Fact]
    public void Narrate_ZeroCountsAreValid()
    {
        var item = Assert.Single(DrainItems(Narrate([Scheduled(0), Drain(6, "active=0 liveGates=0")])));

        Assert.Contains("0 running workers and 0 live gates", item.Phrase);
    }

    [Fact]
    public void Narrate_InterleavedGoalsKeepTheirOwnScheduleAndCounts()
    {
        var first = Scheduled(0);
        var second = Scheduled(3, Other);
        var items = DrainItems(Narrate([first, second, Drain(6),
            Drain(15, "active=1 liveGates=1", Other), Drain(20)]));

        Assert.Equal(2, items.Length);
        var own = Assert.Single(items, item => item.GoalId == Goal);
        var other = Assert.Single(items, item => item.GoalId == Other);
        Assert.Equal(first, own.Evidence![0]);
        Assert.Equal(second, other.Evidence![0]);
        Assert.Contains("elapsed " + ConsoleDurationFormat.Format(TimeSpan.FromSeconds(20)), own.Phrase);
        Assert.Contains("elapsed " + ConsoleDurationFormat.Format(TimeSpan.FromSeconds(12)), other.Phrase);
        Assert.Contains("1 running worker and 1 live gate", other.Phrase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Narrate_NewScheduleStartsNewWindow(bool closeFirst)
    {
        var first = Scheduled(0);
        var second = Scheduled(60);
        var events = new List<OwnerConductEvent> { first, Drain(6) };
        if (closeFirst) events.Add(Event(12, "loop-handoff", "ACTIVATION_ADOPTED", null));
        events.AddRange([second, Drain(72)]);

        var items = DrainItems(Narrate(events));

        Assert.Equal(2, items.Length);
        Assert.Equal(second, items[0].Evidence![0]);
        Assert.Contains("elapsed " + ConsoleDurationFormat.Format(TimeSpan.FromSeconds(12)), items[0].Phrase);
        Assert.Equal(first, items[1].Evidence![0]);
    }

    [Fact]
    public void Narrate_OrdersChronologicallyAndKeepsSuppliedOrderForTies()
    {
        var scheduled = Scheduled(0);
        var first = Drain(6);
        var last = Drain(6, "active=1 liveGates=1");

        var item = Assert.Single(DrainItems(Narrate([first, scheduled, last])));

        Assert.Equal(new[] { scheduled, first, last }, item.Evidence);
        Assert.Contains("1 running worker and 1 live gate", item.Phrase);
        Assert.Empty(DrainItems(Narrate([Drain(0), scheduled])));
    }

    [Fact]
    public void Narrate_RefreshingSnapshotUpdatesOneWindowWithoutHiddenState()
    {
        var scheduled = Scheduled(0);
        var first = Drain(6);
        var last = Drain(363);
        var initial = Assert.Single(DrainItems(Narrate([scheduled, first])));
        var updated = Assert.Single(DrainItems(Narrate([scheduled, first, last])));

        Assert.Equal(initial.Evidence![0], updated.Evidence![0]);
        Assert.Equal(first.Timestamp, initial.Timestamp);
        Assert.Equal(last.Timestamp, updated.Timestamp);
        Assert.Equal(initial.Phrase, Assert.Single(DrainItems(Narrate([scheduled, first]))).Phrase);
        Assert.Empty(DrainItems(Narrate([last])));
    }

    private static IReadOnlyList<OwnerConsoleActivityItem> Narrate(IEnumerable<OwnerConductEvent> events) =>
        OwnerActivityNarrator.Narrate(events, id => id == Other ? "Export" : "Search");

    private static OwnerConsoleActivityItem[] DrainItems(IEnumerable<OwnerConsoleActivityItem> items) =>
        items.Where(item => item.Evidence?.Any(value =>
            OwnerEventFields.Head(value) == "LOOP_RELAUNCH_DRAIN") == true).ToArray();

    private static OwnerConductEvent Scheduled(int seconds, string goal = Goal) =>
        Event(seconds, "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED tick=1", goal);

    private static OwnerConductEvent Drain(int seconds,
        string fields = "active=2 admitting=false reason=dispatch-handling-changed liveGates=0", string goal = Goal) =>
        Event(seconds, "loop-relaunch", "LOOP_RELAUNCH_DRAIN tick=2 " + fields, goal);

    private static OwnerConductEvent Event(int seconds, string kind, string detail, string? goal) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(seconds), kind, goal, detail + (goal is null ? "" : " goal=" + goal));
}
