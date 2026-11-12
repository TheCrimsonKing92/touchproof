using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure offer-policy inputs; no shared state is changed.
public sealed class GoalWorktreeCleanupLaneSplitTests
{
    [Xunit.Fact]
    public void OriginalMeasuredLaneIsRejectedAsRemoteSlower()
    {
        var decision = RemoteLaneOfferPolicy.Decide(new RemoteLaneOfferInputs(
            "Goal worktree cleanup", 458, 3, 458, 853, 37, 675));

        Xunit.Assert.False(decision.Offer);
        Xunit.Assert.Equal("remote-slower", decision.Reason);
    }
}
