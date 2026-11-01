using Mcg.AgentOrchestrator.Core;
using Xunit;

[Collection(TestCollections.LandingGitRunner)]
public sealed class PullRequestLandingDeliveryFailureTests
{
    [Theory]
    [InlineData("origin", "no-github-origin")]
    [InlineData("push", "push-rejected")]
    [InlineData("auth", "gh-not-signed-in")]
    [InlineData("create", "gh-failed")]
    public void Publisher_failure_escalates_with_kind_and_never_records_delivery(string stage, string expected)
    {
        using var fixture = new PullRequestLandingTestFixture();
        fixture.Forge.Failure = stage;

        var result = fixture.Land();

        Assert.Contains("pull request delivery failed: " + expected, Assert.IsType<LandingDecision.Escalate>(result.Decision).Reason);
        Assert.Null(fixture.Goal.DeliveredForReview);
        Assert.False(result.MainAdvanced);
        Assert.Equal(fixture.Main, LandingExecutorTests.ReadGit(fixture.Repository, "rev-parse", "main"));
    }
}
