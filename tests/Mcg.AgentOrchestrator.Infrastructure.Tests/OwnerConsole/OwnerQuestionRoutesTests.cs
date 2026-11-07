using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: all events and route state belong to each test.
public sealed class OwnerQuestionRoutesTests
{
    [Fact]
    public void Route_EarlierAndLaterCandidates_SelectsLatestAtOrBefore()
    {
        var first = Author(1, "question-id=q1");
        var latest = Author(3, "question-id=q1");
        var escalation = Escalation(3, "q1");
        var later = Author(4, "question-id=q1");
        var routes = new OwnerQuestionRoutes([first, latest, escalation, later]);

        Assert.Same(latest, routes.Route(escalation));
        Assert.True(routes.Folded(latest));
        Assert.False(routes.Folded(first));
        Assert.False(routes.Folded(later));
    }

    [Fact]
    public void Route_NoEarlierCandidate_SelectsEarliestLater()
    {
        var escalation = Escalation(1, "q2");
        var first = Author(2, "question-id=q2");
        var later = Author(3, "question-id=q2");
        var routes = new OwnerQuestionRoutes([escalation, first, later]);

        Assert.Same(first, routes.Route(escalation));
        Assert.True(routes.Folded(first));
        Assert.False(routes.Folded(later));
    }

    [Fact]
    public void Route_TwoMatchingEscalations_ConsumesRouteOnlyOnce()
    {
        var author = Author(1, "question-id=q3");
        var first = Escalation(2, "q3");
        var second = Escalation(3, "q3");
        var routes = new OwnerQuestionRoutes([author, first, second]);

        Assert.Same(author, routes.Route(first));
        Assert.Null(routes.Route(second));
        Assert.True(routes.Folded(author));
    }

    [Fact]
    public void Route_GoalPrefixedItem_MatchesUnprefixedQuestionId()
    {
        var author = Author(1, "item=11111111:Q4");
        var escalation = Escalation(2, "q4");
        var routes = new OwnerQuestionRoutes([author, escalation]);

        Assert.Same(author, routes.Route(escalation));
        Assert.True(routes.Folded(author));
    }

    [Fact]
    public void Folded_DifferentQuestionOrGoal_LeavesRouteUnfolded()
    {
        var author = Author(1, "question-id=q5");
        var differentQuestion = Escalation(2, "q6");
        var differentGoal = Escalation(3, "q5") with { GoalId = "22222222" };
        var routes = new OwnerQuestionRoutes([author, differentQuestion, differentGoal]);

        Assert.False(routes.Folded(author));
        Assert.Null(routes.Route(differentQuestion));
        Assert.Null(routes.Route(differentGoal));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    public void Route_EqualTimeCandidates_PreservesFirstInputTieBreak(int routeAt, int escalationAt)
    {
        var first = Author(routeAt, "question-id=q7 recipient=owner");
        var second = Author(routeAt, "question-id=q7 recipient=operator");
        var escalation = Escalation(escalationAt, "q7");
        OwnerConductEvent[] events = routeAt <= escalationAt ? [first, second, escalation] : [escalation, first, second];
        var routes = new OwnerQuestionRoutes(events);

        Assert.Same(first, routes.Route(escalation));
        Assert.False(routes.Folded(second));
    }

    [Fact]
    public void Route_WithoutIds_RequiresEarlierTimeAndCompatibleAgent()
    {
        var compatible = Author(1, "agent=author-a");
        var otherAgent = Author(2, "agent=author-b");
        var escalation = new OwnerConductEvent(DateTimeOffset.UnixEpoch.AddSeconds(3), "goal-escalation",
            "11111111", "author-owner-question agent=author-a");
        var later = Author(4, "agent=author-a");
        var routes = new OwnerQuestionRoutes([compatible, otherAgent, escalation, later]);

        Assert.Same(compatible, routes.Route(escalation));
        Assert.False(routes.Folded(otherAgent));
        Assert.False(routes.Folded(later));
    }

    private static OwnerConductEvent Author(int seconds, string fields) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(seconds), "author", "11111111", "kind=ask-owner " + fields);
    private static OwnerConductEvent Escalation(int seconds, string questionId) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(seconds), "goal-escalation", "11111111",
            "author-owner-question question-id=" + questionId + " question=Retry?");
}
