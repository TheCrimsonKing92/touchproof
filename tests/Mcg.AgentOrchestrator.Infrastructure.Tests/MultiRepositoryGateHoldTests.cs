using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: repositories, attention and state stores are case-owned, gate/landing are delegates.
public sealed class MultiRepositoryGateHoldTests
{
    [Theory]
    [InlineData("acceptance")]
    [InlineData("land")]
    public void Execute_ManualGateOrLanding_PreservesMultiRepositoryHold(string command)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var goal = fixture.Goal;
        if (command == "land") fixture.Gate.Run(goal, GoalLifecycleState.Verified, fixture.RunHome);
        Assert.True(CliCommandHandlers.Execute([command, goal.Id.Value], fixture.Context()));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Contains(MultiRepositoryLandingHold.Kind, goal.LandingBlocker!.Kind);
        Assert.Equal("operator", goal.LandingBlocker.NextActor);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.True(fixture.Gate.Store.TryRead(goal.Id)!.Passed);
    }

    [Fact]
    public void Execute_AcceptAlias_HoldsBeforeGateMergeOrCleanup()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var goal = fixture.CreateGoal();
        ConductorDriverTests.PassVerification(fixture.Kernel, goal, goal.Tasks.Single());
        var worktree = fixture.CreateHomeWorktree(goal);
        var candidate = GoalRepositoryWorktreeProvisionerFixture.CommitChange(worktree);
        var main = fixture.Home.Git("rev-parse", "HEAD");

        Assert.False(CliCommandHandlers.Execute(["accept", goal.Id.Value, "--skip-verify",
            "--no-record", "--autonomy", AutonomyPolicy.SupervisedAuto.Name], fixture.Context()));

        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.DoesNotContain(MultiRepositoryLandingHold.Kind, goal.LandingBlocker?.Kind ?? "");
        Assert.Null(new MultiRepositoryAcceptanceReceiptStore(fixture.Workspace.OrchestratorDirectory).TryRead(goal.Id));
        Assert.Equal(main, fixture.Home.Git("rev-parse", "HEAD"));
        Assert.Equal(candidate, GoalRepositoryWorktreeProvisionerFixture.Run(worktree, "rev-parse", "HEAD"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildQueue_FastForwardableGoal_HoldsOnlyDeclaredRepositories(bool declared)
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var goal = fixture.CreateGoal(declared: declared);
        ConductorDriverTests.PassVerification(fixture.Kernel, goal, goal.Tasks.Single());
        var worktree = fixture.CreateHomeWorktree(goal);
        var candidate = GoalRepositoryWorktreeProvisionerFixture.CommitChange(worktree);
        var main = fixture.Home.Git("rev-parse", "HEAD");
        Assert.NotEqual(main, candidate);
        fixture.Home.Git("merge-base", "--is-ancestor", "HEAD", GoalWorktrees.BranchName(goal.Id));

        var item = Assert.Single(AcceptanceQueuePlanner.Build(fixture.Kernel,
            fixture.Home.CheckoutPath, AutonomyPolicy.SupervisedAuto, "main").Items);
        Assert.True(item.HasBranchDiff);
        Assert.Equal(false, item.WorktreeDirty);
        Assert.Equal(declared ? AcceptanceQueueDisposition.Held : AcceptanceQueueDisposition.Ready,
            item.Disposition);
        if (!declared) return;

        Assert.Equal(MultiRepositoryLandingHold.Kind, item.Reason);
        Assert.Equal(MultiRepositoryLandingHold.NextAction, item.SuggestedCommand);
        CliCommandHandlers.Execute(["acceptance-queue", "--apply", "--confirm-acceptance-queue",
            "--autonomy", AutonomyPolicy.SupervisedAuto.Name], fixture.Context());
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(main, fixture.Home.Git("rev-parse", "HEAD"));
        Assert.Equal(candidate, GoalRepositoryWorktreeProvisionerFixture.Run(worktree, "rev-parse", "HEAD"));
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Reconcile_UndeclaredGoal_NeverWritesAttention(bool verified, bool completed)
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var goal = fixture.CreateGoal(declared: false);
        if (verified) ConductorDriverTests.PassVerification(fixture.Kernel, goal, goal.Tasks.Single());
        if (completed) fixture.Kernel.CompleteGoal(goal.Id, "Fixture completed.");
        CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        // The previous TryResolveAsync path attempts BEGIN IMMEDIATE on this read-only store.
        var store = CollaborationItemStore.OpenExisting(fixture.Workspace.OrchestratorDirectory);
        var attention = new MultiRepositoryLandingHoldAttention(store);

        attention.Reconcile(goal);
        attention.ReconcileAll(fixture.Kernel.Goals);

        Assert.Empty(store.GetAttentionQueueAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public async Task AdvanceOnce_CurrentReceipt_HoldsAcrossReloadWithoutRegatingOrLanding()
    {
        using var acceptance = new MultiRepositoryAcceptanceGateFixture();
        var fixture = acceptance.Repositories;
        var goal = acceptance.Goal;
        Assert.Equal(GoalStatus.Verified, goal.Status);
        var driver = acceptance.Driver();
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);

        for (var round = 0; round < 2; round++)
        {
            var kernel = round == 0 ? fixture.Kernel : await repository.LoadAsync();
            var current = kernel.GetGoal(goal.Id);
            var result = driver.AdvanceOnce(current, ConductorAutonomyPolicy.Conservative);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(MultiRepositoryLandingHold.Kind, held.Decision!.DiscriminatingEvidence);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Null(driver.TryBuildParallelAcceptanceCandidate(current,
                ConductorAutonomyPolicy.Conservative, 0, out var reason));
            Assert.Equal(MultiRepositoryLandingHold.Kind, reason);
            Assert.Equal(MultiRepositoryLandingHold.Kind, Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
                driver.ProjectGateReadyCandidate(current, ConductorAutonomyPolicy.Conservative)).HoldReason);
            Assert.Equal(MultiRepositoryLandingHold.Kind, Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
                driver.ProjectCachedGateReadyCandidate(current, ConductorAutonomyPolicy.Conservative)).HoldReason);
            Assert.Equal(MultiRepositoryLandingHold.Kind, Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
                driver.ProjectInReviewCohortPartner(current, ConductorAutonomyPolicy.Conservative)).HoldReason);
            ConductorLandingBlockerRecorder.Observe(kernel, current, held,
                DateTimeOffset.Parse("2026-11-07T15:00:00Z"), []);
            Assert.Equal("operator", current.LandingBlocker!.NextActor);
            Assert.Equal("wait-for-the-landing-slice", current.LandingBlocker.NextVerb);
            ConductorGateAttention.ReconcileOutcome(kernel, driver, current, held);
            await repository.SaveAsync(kernel);
        }

        Assert.Equal(2, acceptance.Calls.Count);
        Assert.Equal(0, acceptance.LandingCalls);
        var persisted = (await repository.LoadAsync()).GetGoal(goal.Id);
        var attention = await CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory).GetAttentionQueueAsync();
        var item = Assert.Single(attention, item => item.CorrelationKey == MultiRepositoryLandingHoldAttention.Prefix + goal.Id.Value);
        Assert.Contains("operator", item.Body);
        Assert.Contains(MultiRepositoryLandingHold.NextAction, item.Body);
        using var output = new StringWriter();
        CliConductorStatusReader.Print(new(null, new("unavailable", null, null), null,
            new("no-build", null, null), null,
            new(true, [new(goal.Id.Value, "Verified", "none", persisted.LandingBlocker)], 0, 0),
            null, null, new("unavailable", null, [])), output);
        Assert.Contains(MultiRepositoryLandingHold.Kind, output.ToString());
        Assert.Contains("next=operator", output.ToString());
    }
}
