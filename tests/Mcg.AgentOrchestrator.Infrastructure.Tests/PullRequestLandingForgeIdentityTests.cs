using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Real, fixture-owned git repositories; uses the existing landing lane. Forge calls are fakes.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PullRequestLandingForgeIdentityTests
{
    [Fact]
    public void Deliver_ConfiguredIdentity_PersistsActorInRecordHoldAndTimeline()
    {
        using var fixture = new PullRequestLandingTestFixture();
        var workspace = fixture.Workspace with { ForgeIdentity = "configured-settings" };
        var provider = new Provider();
        var result = LandingExecutor.Execute(fixture.Kernel, fixture.Goal, workspace,
            pullRequestPublisher: new GitHubPullRequestPublisher(fixture.Forge, provider));

        Assert.Equal("configured-settings", provider.Path);
        Assert.Equal("delivered for review: " + PullRequestLandingTestFixture.Url + " as mcg-orchestrator[bot]", result.Message);
        Assert.Equal("mcg-orchestrator[bot]", fixture.Goal.DeliveredForReview!.ActingLogin);
        Assert.Equal(result.Message, fixture.Goal.CurrentHold!.Blocker);
        Assert.Contains(fixture.Goal.Timeline, item => item.Message == result.Message);

        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        repository.SaveAsync(fixture.Kernel).GetAwaiter().GetResult();
        var restored = repository.LoadAsync().GetAwaiter().GetResult();
        var goal = restored.GetGoal(fixture.Goal.Id);
        Assert.Equal("mcg-orchestrator[bot]", goal.DeliveredForReview!.ActingLogin);
        Assert.Equal(result.Message, goal.CurrentHold!.Blocker);
        var callCount = fixture.Forge.Calls.Count;
        var repeated = LandingExecutor.Execute(restored, goal, workspace,
            pullRequestPublisher: new GitHubPullRequestPublisher(fixture.Forge, provider));
        Assert.Equal(result.Message, repeated.Message);
        Assert.Equal(callCount, fixture.Forge.Calls.Count);
    }

    private sealed class Provider : IForgeIdentityProvider
    {
        public string? Path { get; private set; }
        public ForgeIdentityPublish Acquire(string settingsPath)
        {
            Path = settingsPath;
            return new(new Dictionary<string, string> { ["GH_TOKEN"] = "ghs_testOnly" }, "mcg-orchestrator[bot]");
        }
    }
}
