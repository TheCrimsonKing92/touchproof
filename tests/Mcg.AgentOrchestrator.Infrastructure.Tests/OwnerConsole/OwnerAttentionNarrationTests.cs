using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: narration uses only local observations and hand-built events.
public sealed class OwnerAttentionNarrationTests
{
    [Fact]
    public void Narrate_LiveQuestion_PreservesDecisionFieldsAndOriginalQuestion()
    {
        var entry = Entry();
        var item = Assert.Single(new OwnerAttentionNarration([], _ => "Search").Narrate([entry]));

        Assert.Equal("Needs you: 11111111 Keep the issue direction?", item.Phrase);
        Assert.Equal("Keep the issue direction?", item.Subject);
        Assert.Equal("Keep the issue direction?", item.Why);
        Assert.Equal("Work waits for your answer.", item.Next);
        Assert.Equal("decision", item.Tag);
        Assert.Equal("q1", item.OwnerQuestionId);
        Assert.Equal("owner-question", item.Kind);
        Assert.Equal(entry.FirstSeen, item.Timestamp);
        Assert.Equal(entry.Question.GoalId, item.GoalId);
        Assert.Equal(entry.Question.Text, item.Question);
        Assert.Null(item.Resolution);
        var source = Assert.Single(item.Evidence!);
        Assert.Equal(entry.FirstSeen, source.Timestamp);
        Assert.Equal(entry.Question.GoalId, source.GoalId);
    }

    [Fact]
    public void Narrate_ResolvedQuestion_ReturnsBothItemsWithOperatorResolution()
    {
        var entry = Entry() with { ResolvedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) };
        OwnerConductEvent[] events = [new(entry.ResolvedAt.Value, "goal-lifecycle", "11111111",
            "HumanInputReceived answer-target=11111111:q1 resolution-actor=operator")];
        var items = new OwnerAttentionNarration(events, _ => "Search").Narrate([entry]);

        Assert.Equal(2, items.Count);
        var needsYou = items[0];
        var resolved = items[1];
        Assert.Equal("Needs you: 11111111 Keep the issue direction?", needsYou.Phrase);
        Assert.Equal(entry.FirstSeen, needsYou.Timestamp);
        Assert.Equal("decision", needsYou.Tag);
        Assert.Equal("Resolved: 11111111 Search: Keep the issue direction?", resolved.Phrase);
        Assert.Equal("owner-question-resolved", resolved.Kind);
        Assert.Equal(entry.ResolvedAt.Value, resolved.Timestamp);
        Assert.Equal("The conductor can continue.", resolved.Next);
        Assert.Equal(new OwnerConsoleTitleSpan("Resolved: 11111111 ".Length, "Search".Length, 0),
            Assert.Single(resolved.Spans!.Titles));
        foreach (var item in items)
        {
            Assert.Equal(new OwnerActivityResolution(entry.ResolvedAt.Value, Actor: "operator"), item.Resolution);
            Assert.Equal("q1", item.OwnerQuestionId);
            Assert.Equal(entry.Question.GoalId, item.GoalId);
            Assert.Equal(entry.Question.Text, item.Question);
            Assert.Equal("Keep the issue direction?", item.Subject);
        }
    }

    [Fact]
    public void Narrate_ResolvedWithoutTitleOrAnswer_UsesPrefixAndRecordedRemovalTime()
    {
        var entry = Entry() with { ResolvedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) };
        var items = new OwnerAttentionNarration([], _ => "11111111").Narrate([entry]);

        Assert.Equal(2, items.Count);
        Assert.Equal("Resolved: 11111111 Keep the issue direction?", items[1].Phrase);
        Assert.Null(items[1].Spans);
        Assert.All(items, item => Assert.Equal(new OwnerActivityResolution(entry.ResolvedAt.Value), item.Resolution));
    }

    [Fact]
    public void Narrator_SameTimeOtherKind_PutsResolvedItemFirst()
    {
        var entry = Entry() with { ResolvedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) };
        OwnerConductEvent[] events = [new(entry.ResolvedAt.Value, "goal-stalled", "22222222",
            "GOAL_STALLED repeatedForSeconds=300 blocker=waiting_for_approval")];

        var items = OwnerActivityNarrator.Narrate(events, _ => "Search", attention: [entry]);

        Assert.Equal(new[] { "owner-question-resolved", "goal-stalled", "owner-question" },
            items.Select(item => item.Kind));
        Assert.Equal(entry.ResolvedAt.Value, items[0].Timestamp);
        Assert.Equal(items[0].Timestamp, items[1].Timestamp);
        Assert.Equal(entry.FirstSeen, items[2].Timestamp);
    }

    [Fact]
    public void Narrate_NoAttention_ReturnsNoItems() =>
        Assert.Empty(new OwnerAttentionNarration([], _ => "Search").Narrate(null));

    private static OwnerAttentionObservation Entry() =>
        new(new("q1", "11111111", OwnerQuestionKind.HumanInput, "Keep the ticket direction?"), DateTimeOffset.UnixEpoch);
}
