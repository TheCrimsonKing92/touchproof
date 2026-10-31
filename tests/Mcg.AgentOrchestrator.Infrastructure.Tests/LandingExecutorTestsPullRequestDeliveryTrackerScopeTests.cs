using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Serialized with the Landing lane's shared GitRunner seam; repositories are private.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsPullRequestDeliveryTrackerScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Home_and_direct_projects_are_not_polled(bool home)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var workspace = home ? fixture.Workspace with { IsProjectScoped = false } :
            fixture.Workspace with { Delivery = ProjectDelivery.Direct };
        Assert.Empty(fixture.CreateTracker(workspace).Service([fixture.Goal]));
        Assert.Equal(0, fixture.Forge.Reads);
        Assert.Empty(fixture.Forge.Fetches);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
    }

    [Fact]
    public void Undelivered_goal_is_not_polled()
    {
        using var fixture = new PullRequestLandingTestFixture();
        var forge = new PullRequestTrackingTestFixture.Runner();
        var tracker = new PullRequestDeliveryTracker(fixture.Kernel, fixture.Workspace,
            new GitHubPullRequestStatusReader(forge), forge, CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory));
        Assert.Null(fixture.Goal.DeliveredForReview);
        Assert.Empty(tracker.Service([fixture.Goal]));
        Assert.Equal(0, forge.Reads);
        Assert.Empty(forge.Fetches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Home_and_direct_landing_retain_native_promotion(bool home)
    {
        using var fixture = new PullRequestLandingTestFixture();
        var workspace = fixture.Workspace with { IsProjectScoped = !home, Delivery = ProjectDelivery.Direct };
        var result = LandingExecutor.Execute(fixture.Kernel, fixture.Goal, workspace,
            pullRequestPublisher: new GitHubPullRequestPublisher(fixture.Forge));
        Assert.True(result.MainAdvanced, result.Message);
        Assert.Empty(fixture.Forge.Calls);
        Assert.Equal(GoalPromotionFact.SoloProvenance,
            new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).ReadPromotionFactAsync(fixture.Goal.Id).GetAwaiter().GetResult()!.Provenance);
    }
}
