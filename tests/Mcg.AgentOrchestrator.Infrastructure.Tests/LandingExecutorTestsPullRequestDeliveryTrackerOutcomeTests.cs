using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Serialized with the Landing lane's shared GitRunner seam; repositories are private.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsPullRequestDeliveryTrackerOutcomeTests
{
    [Fact]
    public void Closed_unmerged_escalates_once_and_can_later_merge()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        fixture.Forge.Json = """{"state":"CLOSED","mergedAt":null,"mergeCommit":null,"closedAt":"2026-10-31T12:00:00Z","url":"https://github.com/octo/sample/pull/7"}""";
        var reason = "pull request closed unmerged: " + PullRequestLandingTestFixture.Url;
        Assert.Contains(fixture.Goal.Id, fixture.Tick());
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Null(fixture.Fact());
        Assert.Contains(fixture.Attention.GetAttentionQueueAsync().GetAwaiter().GetResult(), item => item.Body.Contains(reason));
        fixture.Elapsed = TimeSpan.FromMinutes(2);
        Assert.Empty(fixture.CreateTracker().Service([fixture.Goal]));
        Assert.Single(fixture.Goal.Timeline.Where(item => item.Message == reason));
        fixture.Merged();
        fixture.Elapsed = TimeSpan.FromMinutes(4);
        fixture.Tick();
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("fetch")]
    [InlineData("absent")]
    public void Unavailable_or_unconfirmed_trunk_leaves_state_unchanged(string fault)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        fixture.Merged();
        fixture.Forge.Unavailable = fault == "reader";
        fixture.Forge.FetchFails = fault == "fetch";
        if (fault == "absent") fixture.Git("update-ref", "refs/remotes/origin/main", fixture.Delivery.Main);
        var before = fixture.Kernel.ExportSnapshot();
        Assert.Empty(fixture.Tick());
        fixture.Elapsed = TimeSpan.FromSeconds(119);
        Assert.Empty(fixture.Tick());
        Assert.Equal(1, fixture.Forge.Reads);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot()));
        Assert.Null(fixture.Fact());
        Assert.Empty(fixture.Attention.GetAttentionQueueAsync().GetAwaiter().GetResult());
        fixture.Elapsed = TimeSpan.FromMinutes(2);
        fixture.Tick();
        Assert.Equal(2, fixture.Forge.Reads);
    }

    [Theory]
    [InlineData("divergence")]
    [InlineData("guard")]
    [InlineData("update")]
    [InlineData("checkout")]
    public void Local_update_failure_still_lands_with_one_reconcile_item(string failure)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        if (failure == "divergence") LandingExecutorTests.AppendCommit(fixture.Workspace.ExecutionDirectory, "local.txt", "local");
        if (failure == "guard") File.AppendAllText(Path.Combine(fixture.Workspace.ExecutionDirectory, "README.md"), "dirty");
        var tip = fixture.Git("rev-parse", "main");
        var runner = LandingExecutor.GitRunner;
        var attempted = false;
        try
        {
            LandingExecutor.GitRunner = (root, args) =>
            {
                if ((failure == "update" && args[0] == "update-ref") || (failure == "checkout" && args[0] == "read-tree"))
                { attempted = true; return new GitCli.GitResult(1, "", "injected local failure"); }
                return runner(root, args);
            };
            fixture.Merged();
            fixture.Tick();
        }
        finally { LandingExecutor.GitRunner = runner; }
        Assert.Equal(failure is "update" or "checkout", attempted);
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        Assert.Equal(fixture.Delivery.Candidate, fixture.Fact()!.PromotedMainRevision);
        if (failure != "checkout") Assert.Equal(tip, fixture.Git("rev-parse", "main"));
        var item = Assert.Single(fixture.ReconcileItems());
        Assert.Contains(tip, item.Body);
        Assert.Contains(fixture.Delivery.Candidate, item.Body);
        Assert.Contains(PullRequestLandingTestFixture.Url, item.Body);
        Assert.Contains(fixture.Workspace.ExecutionDirectory, item.Body);
        fixture.CreateTracker().Service([fixture.Goal]);
        Assert.Equal(item.Id, Assert.Single(fixture.ReconcileItems()).Id);
    }
}
