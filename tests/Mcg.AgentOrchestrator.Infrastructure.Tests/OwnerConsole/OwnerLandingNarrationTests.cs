using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: every test owns its event sequence and fold state.
public sealed class OwnerLandingNarrationTests
{
    [Fact]
    public void Narrate_ContiguousTrain_GroupsTextEvidenceAndLatestGoals()
    {
        OwnerConductEvent[] events = [Landing(0, "11111111", "7"), Landing(1, "22222222", "7")];
        var rule = new OwnerLandingNarration(events, id => id == "11111111" ? "T1" : "T2");

        var item = Assert.IsType<OwnerConsoleActivityItem>(rule.Narrate(0));

        Assert.Equal("Landed together: T1 (11111111), T2 (22222222)", item.Phrase);
        Assert.Equal("The work passed its checks and was added to main.", item.Why);
        Assert.Equal("The conductor will continue with the remaining work.", item.Next);
        Assert.Equal(events, item.Evidence);
        Assert.Equal(new[] { "11111111", "22222222" }, item.GoalPrefixes);
        Assert.Equal(new[] { "T1", "T2" }, rule.MostRecentLanding);
        Assert.Equal(item.GoalPrefixes, rule.MostRecentLandingPrefixes);
        Assert.Equal(new[] { "T1", "T2" }, item.Spans!.Titles
            .Select(span => item.Phrase.Substring(span.Start, span.Length)));
        Assert.Null(rule.Narrate(1));
    }

    [Theory]
    [InlineData(0, null, true)]
    [InlineData(1, null, false)]
    [InlineData(0, "8", false)]
    public void Narrate_TrainBoundary_UsesTickOrTimestamp(int seconds, string? tick, bool grouped)
    {
        OwnerConductEvent[] events = [Landing(0, "11111111", tick is null ? null : "7"),
            Landing(seconds, "22222222", tick)];
        var rule = new OwnerLandingNarration(events, id => id!);

        var item = Assert.IsType<OwnerConsoleActivityItem>(rule.Narrate(0));

        Assert.Equal(grouped ? 2 : 1, item.Evidence!.Count);
        if (grouped) Assert.Null(rule.Narrate(1));
        else
        {
            Assert.Null(item.GoalPrefixes);
            Assert.NotNull(rule.Narrate(1));
        }
    }

    [Fact]
    public void Narrate_InterveningEvent_EndsTrainDespiteMatchingTick()
    {
        OwnerConductEvent[] events = [Landing(0, "11111111", "7"),
            new(DateTimeOffset.UnixEpoch.AddSeconds(1), "loop-start", null, "LOOP_START"),
            Landing(2, "22222222", "7")];
        var rule = new OwnerLandingNarration(events, id => id!);

        Assert.Single(Assert.IsType<OwnerConsoleActivityItem>(rule.Narrate(0)).Evidence!);
        Assert.Single(Assert.IsType<OwnerConsoleActivityItem>(rule.Narrate(2)).Evidence!);
    }

    private static OwnerConductEvent Landing(int seconds, string goal, string? tick) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(seconds), "loop-relaunch", goal,
            "LOOP_RELAUNCH_SCHEDULED" + (tick is null ? "" : " tick=" + tick));
}
