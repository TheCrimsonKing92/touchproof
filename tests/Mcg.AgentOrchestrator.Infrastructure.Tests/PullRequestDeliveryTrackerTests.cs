using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Serialized with the Landing lane's shared GitRunner seam; repositories are private.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PullRequestDeliveryTrackerTests
{
    [Fact]
    public void Open_cadence_then_merge_advances_and_completes()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        Assert.Empty(fixture.Tick());
        fixture.Elapsed = TimeSpan.FromSeconds(119);
        Assert.Empty(fixture.Tick());
        Assert.Equal(1, fixture.Forge.Reads);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.NotNull(fixture.Goal.CurrentHold);
        Assert.Empty(fixture.Forge.Fetches);
        Assert.Null(fixture.Fact());
        fixture.Merged();
        fixture.Elapsed = TimeSpan.FromMinutes(2);

        Assert.Contains(fixture.Goal.Id, fixture.Tick());

        Assert.Equal(2, fixture.Forge.Reads);
        Assert.Equal(["fetch", "--no-tags", "origin", "+refs/heads/main:refs/remotes/origin/main"], Assert.Single(fixture.Forge.Fetches));
        Assert.Equal(fixture.Delivery.Candidate, fixture.Git("rev-parse", "main"));
        Assert.Equal(fixture.Delivery.Candidate, fixture.Git("rev-parse", "HEAD"));
        Assert.Equal("", fixture.Git("status", "--porcelain", "--untracked-files=no"));
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        var fact = Assert.IsType<GoalPromotionFact>(fixture.Fact());
        Assert.Equal(GoalPromotionFact.PullRequestMergedProvenance, fact.Provenance);
        Assert.Equal(fixture.Delivery.Candidate, fact.PromotedMainRevision);
        Assert.Equal(fixture.Delivery.Candidate, fact.CandidateRevision);
        Assert.Equal(fixture.Git("rev-parse", fixture.Delivery.Candidate + "^1"), fact.PriorMainRevision);
        Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(GoalOperationJournal.Read(fixture.Workspace.ExecutionDirectory, fixture.Goal.Id)));
        Assert.Empty(fixture.ReconcileItems());
        var receipt = Assert.Single(fixture.Receipts);
        Assert.Equal(fixture.Goal.Id.Value, receipt.GoalId);
        Assert.Equal(fixture.Delivery.Candidate, receipt.LandingSha);
        Assert.Contains("src/delivery.txt", receipt.ChangedFiles);
        Assert.Empty(fixture.CreateTracker().Service([fixture.Goal]));
        Assert.Equal(2, fixture.Forge.Reads);
        Assert.Equal(fact, fixture.Fact());
    }

    [Fact]
    public void Reentrant_tick_cannot_read_the_same_delivery_twice()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var callbacks = 0;
        fixture.Forge.OnRead = () => { callbacks++; Assert.Empty(fixture.Tick()); };
        fixture.Tick();
        Assert.Equal(1, callbacks);
        Assert.Equal(1, fixture.Forge.Reads);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
    }

    [Fact]
    public void Already_contained_merge_preserves_later_local_commits()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        fixture.Git("merge", "--ff-only", fixture.Delivery.Candidate);
        LandingExecutorTests.AppendCommit(fixture.Workspace.ExecutionDirectory, "later.txt", "later");
        var tip = fixture.Git("rev-parse", "main");
        fixture.Merged();
        fixture.Tick();
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        Assert.Equal(tip, fixture.Git("rev-parse", "main"));
        Assert.Equal(fixture.Delivery.Candidate, fixture.Fact()!.PromotedMainRevision);
        Assert.Empty(fixture.ReconcileItems());
    }
}
