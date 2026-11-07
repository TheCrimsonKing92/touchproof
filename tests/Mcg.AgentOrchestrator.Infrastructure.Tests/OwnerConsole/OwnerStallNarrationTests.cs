using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each case owns its held set and events.
public sealed class OwnerStallNarrationTests
{
    [Fact]
    public void TryNarrate_HeldGoal_PreservesTextAndSuppressesRepeatedStall()
    {
        var source = Event("GOAL_STALLED heldForSeconds=600 blocker=receipt-missing");
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(OwnerStallNarration.TryNarrate(source, held, _ => "T", out var item));

        Assert.Equal("Waiting: T has been held 10 min: waiting for verification to finish", item.Sentence);
        Assert.Equal("waiting for verification to finish", item.Why);
        Assert.Equal("Work resumes when the blocker clears.", item.Next);
        Assert.Equal("Check DECISIONS for an owner question; otherwise the conductor will retry when it can.", item.Act);
        Assert.Equal(new OwnerConsoleTitleSpan("Waiting: ".Length, 1, 0), Assert.Single(item.Spans.Titles));
        Assert.Contains("11111111", held);
        Assert.False(OwnerStallNarration.TryNarrate(source, held, _ => throw new InvalidOperationException(), out var duplicate));
        Assert.Null(duplicate);
        held.Clear();
        Assert.True(OwnerStallNarration.TryNarrate(source, held, _ => "T", out _));
    }

    [Theory]
    [InlineData("repeatedForSeconds=120 heldForSeconds=600", 2)]
    [InlineData("heldForSeconds=invalid", 0)]
    [InlineData("heldForSeconds=-60", 0)]
    [InlineData("", 0)]
    public void TryNarrate_Duration_UsesRepeatedFirstAndClampsMinutes(string fields, int minutes)
    {
        Assert.True(OwnerStallNarration.TryNarrate(Event("GOAL_STALLED " + fields + " blocker=x"),
            new HashSet<string>(), _ => "T", out var item));

        Assert.Equal($"Waiting: T has been held {minutes} min: x", item.Sentence);
        Assert.Equal("x", item.Why);
        Assert.Equal("Work resumes when the blocker clears.", item.Next);
    }

    private static OwnerConductEvent Event(string detail) =>
        new(DateTimeOffset.UnixEpoch, "goal-stalled", "11111111-full", detail);
}
