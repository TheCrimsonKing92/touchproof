using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each case owns its event history, attention list and held set.
public sealed class OwnerEscalationNarrationTests
{
    [Theory]
    [InlineData("operator", "the operator")]
    [InlineData("owner", "you")]
    [InlineData("author", "the Author")]
    public void Narrate_QuestionRoute_NamesRecipient(string recipient, string expected)
    {
        OwnerConductEvent[] events = [Event(0, "author", $"kind=ask-owner recipient={recipient} question=Ship it?")];

        var outcome = Emitted(events, 0);

        Assert.Equal($"11111111 question for {expected}: Ship it?", outcome.Sentence);
        Assert.Equal("Ship it?", outcome.Why);
        Assert.Equal("The conductor or operator will handle the next step.", outcome.Next);
        Assert.Equal("No. You can let the conductor continue.", outcome.Act);
        Assert.Null(outcome.Spans);
        Assert.Null(outcome.Resolution);
    }

    [Fact]
    public void Narrate_PairedEscalation_UsesRouteRecipientAndSuppressesFoldedAuthor()
    {
        OwnerConductEvent[] events = [Event(0, "author", "kind=ask-owner question-id=q1 recipient=owner"),
            Event(1, "goal-escalation", "author-owner-question question-id=q1 recipient=operator question=Ship it?")];

        Assert.Null(Narration(events).Narrate(0, Held()));
        var outcome = Emitted(events, 1);

        Assert.Equal("11111111 question for you: Ship it?", outcome.Sentence);
        Assert.Single(OwnerActivityNarrator.Narrate(events, _ => "Search"));
    }

    [Fact]
    public void Narrate_NoQuestionText_UsesTitleSpanAndDefaultAuthor()
    {
        OwnerConductEvent[] events = [Event(0, "author", "kind=ask-owner")];

        var outcome = Emitted(events, 0);

        Assert.Equal("11111111 question for the Author about Search", outcome.Sentence);
        Assert.Equal("about Search", outcome.Why);
        Assert.Equal(new OwnerConsoleTitleSpan("11111111 question for the Author about ".Length, "Search".Length, 0),
            Assert.Single(outcome.Spans!.Titles));
    }

    [Fact]
    public void Narrate_PlanRejection_PreservesReasonAndSentence()
    {
        OwnerConductEvent[] events = [Event(0, "goal-escalation", "owner-review-hold rejection=the plan cites nothing")];

        var outcome = Emitted(events, 0);

        Assert.Equal("11111111 escalated: plan rejected: the plan cites nothing", outcome.Sentence);
        Assert.Equal("plan rejected: the plan cites nothing", outcome.Why);
        Assert.Equal("The conductor or operator will handle the next step.", outcome.Next);
    }

    [Fact]
    public void Narrate_OwnerlessStall_SkipsHeldGoalOtherwiseRequestsStall()
    {
        OwnerConductEvent[] events = [Event(0, "goal-escalation", "ownerless-hold-stalled heldForSeconds=600 blocker=x")];
        var narration = Narration(events);

        Assert.Null(narration.Narrate(0, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "11111111" }));
        Assert.True(Assert.IsType<OwnerEscalationNarration.Narrated>(narration.Narrate(0, Held())).Stall);
        var waiting = Assert.Single(OwnerActivityNarrator.Narrate(events, _ => "Search"));
        Assert.Equal("Waiting: Search has been held 10 min: x", waiting.Phrase);
        var prior = Event(-1, "goal-stalled", "GOAL_STALLED repeatedForSeconds=300 blocker=x");
        Assert.Equal("Waiting: Search has been held 5 min: x",
            Assert.Single(OwnerActivityNarrator.Narrate([prior, .. events], _ => "Search")).Phrase);
    }

    [Fact]
    public void Narrate_AttentionDuplicate_SuppressesOnlySameGoalAndQuestion()
    {
        OwnerConductEvent[] events = [Event(0, "goal-escalation", "steward-owner-question question=Keep it?")];
        OwnerAttentionObservation entry = new(new("q1", "11111111-full", OwnerQuestionKind.HumanInput,
            "Keep it?\nMore detail."), DateTimeOffset.UnixEpoch);

        Assert.Null(Narration(events, [entry]).Narrate(0, Held()));
        Assert.Equal("11111111 escalated: Keep it?", Emitted(events, 0).Sentence);
        var otherGoal = entry with { Question = entry.Question with { GoalId = "22222222" } };
        Assert.Equal("11111111 escalated: Keep it?", Emitted(events, 0, [otherGoal]).Sentence);
        var otherQuestion = entry with { Question = entry.Question with { Text = "Ship it?" } };
        Assert.Equal("11111111 escalated: Keep it?", Emitted(events, 0, [otherQuestion]).Sentence);
    }

    [Fact]
    public void Narrate_LaterAuthorAnswer_RewritesSentenceNextAndResolution()
    {
        var answer = Event(2, "goal-lifecycle", "HumanInputReceived resolution-verb=answer resolution-actor=author");
        OwnerConductEvent[] events = [AuthorQuestion(), answer];

        var outcome = Emitted(events, 0);

        Assert.Equal("11111111 question for the Author: Resolve spec clarification before dispatching planner work" +
            $"; answered by the Author at {answer.Timestamp.ToLocalTime():HH:mm:ss}.", outcome.Sentence);
        Assert.Equal("question for the Author: Resolve spec clarification before dispatching planner work.", outcome.Why);
        Assert.Equal("The Author answered the question; work can continue.", outcome.Next);
        Assert.Equal(new OwnerActivityResolution(answer.Timestamp, Actor: "author"), outcome.Resolution);
    }

    [Theory]
    [InlineData(0, "11111111", "author", "answer")]
    [InlineData(1, "22222222", "author", "answer")]
    [InlineData(1, "11111111", "operator", "answer")]
    [InlineData(1, "11111111", "author", "retry")]
    public void Narrate_UnrelatedResolution_LeavesAuthorQuestionUnanswered(
        int seconds, string goal, string actor, string verb)
    {
        var answer = Event(seconds, "goal-lifecycle", $"HumanInputReceived resolution-verb={verb} resolution-actor={actor}")
            with { GoalId = goal };
        OwnerConductEvent[] events = [AuthorQuestion(), answer];

        var outcome = Emitted(events, 0);

        Assert.Null(outcome.Resolution);
        Assert.Equal("The Author will handle the question.", outcome.Next);
        Assert.Equal("11111111 question for the Author: Resolve spec clarification before dispatching planner work.", outcome.Sentence);
    }

    private static OwnerEscalationNarration.Narrated Emitted(OwnerConductEvent[] events, int index,
        IReadOnlyList<OwnerAttentionObservation>? attention = null)
    {
        var outcome = Assert.IsType<OwnerEscalationNarration.Narrated>(Narration(events, attention).Narrate(index, Held()));
        var item = Assert.Single(OwnerActivityNarrator.Narrate(events, _ => "Search", attention: attention),
            value => value.Timestamp == events[index].Timestamp && value.Kind == events[index].EventKind);
        Assert.Equal(outcome.Sentence, item.Phrase);
        Assert.Equal(outcome.Why, item.Why);
        Assert.Equal(outcome.Next, item.Next);
        Assert.Equal(outcome.Act, item.Act);
        Assert.Equal(outcome.Resolution, item.Resolution);
        Assert.Equal(outcome.Spans?.Titles, item.Spans?.Titles);
        return outcome;
    }

    private static OwnerEscalationNarration Narration(OwnerConductEvent[] events,
        IReadOnlyList<OwnerAttentionObservation>? attention = null) =>
        new(events, new OwnerQuestionRoutes(events), attention, _ => "Search");
    private static HashSet<string> Held() => new(StringComparer.OrdinalIgnoreCase);
    private static OwnerConductEvent AuthorQuestion() => Event(0, "goal-escalation",
        "author-owner-question reason=Resolve spec clarification before dispatching planner work");
    private static OwnerConductEvent Event(int seconds, string kind, string detail) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(seconds), kind, "11111111", detail);
}
