using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: pure narration with test-owned event histories.
public sealed class OwnerLifecycleStageNarrationTests
{
    [Fact]
    public void TryNarrate_WorkerFinding_PreservesSentenceReasonAndNextStep()
    {
        OwnerConductEvent[] events = [Event("TaskFailed role=Developer outcome=finding")];

        Assert.True(OwnerLifecycleStageNarration.TryNarrate(events, 0, "T", null, out var item));

        Assert.Equal("Developer sent T back: a problem needs correction", item.Sentence);
        Assert.Equal("The worker reported a blocking finding.", item.Why);
        Assert.Equal("The Developer will address the finding.", item.Next);
        Assert.Equal(new OwnerConsoleTitleSpan("Developer sent ".Length, 1, 0),
            Assert.Single(item.Spans.Titles));
    }

    [Fact]
    public void TryNarrate_RepeatedDispatch_KeepsAttemptLabelAndTitleSpan()
    {
        OwnerConductEvent[] events = [Event("TaskDispatched role=Developer task=1"),
            Event("TaskDispatched role=Developer task=1")];

        Assert.True(OwnerLifecycleStageNarration.TryNarrate(events, 1, "T", null, out var item));

        Assert.Equal("Developer started on T (attempt 2)", item.Sentence);
        Assert.Equal("The work reached this stage.", item.Why);
        Assert.Equal("Work continues through the remaining checks.", item.Next);
    }

    [Fact]
    public void TryNarrate_MissingCitation_RewritesRejectionInSentenceAndReason()
    {
        OwnerConductEvent[] events = [Event("TaskFailed role=Planner rejection=target citation 'missing.cs' does not exist")];

        Assert.True(OwnerLifecycleStageNarration.TryNarrate(events, 0, "T", null, out var item));

        Assert.Equal("Planner sent T back: plan rejected: cited a file that does not exist: missing.cs", item.Sentence);
        Assert.Equal("plan rejected: cited a file that does not exist: missing.cs", item.Why);
        Assert.Equal("The conductor will retry the worker or request help.", item.Next);
    }

    [Theory]
    [InlineData("HumanInputReceived")]
    [InlineData("HumanInputSuperseded resolution-verb=answer")]
    public void TryNarrate_NoStageLead_EmitsNothing(string detail)
    {
        Assert.False(OwnerLifecycleStageNarration.TryNarrate([Event(detail)], 0, "T", null, out var item));
        Assert.Null(item);
    }

    private static OwnerConductEvent Event(string detail) =>
        new(DateTimeOffset.UnixEpoch, "goal-lifecycle", "11111111", detail);
}
