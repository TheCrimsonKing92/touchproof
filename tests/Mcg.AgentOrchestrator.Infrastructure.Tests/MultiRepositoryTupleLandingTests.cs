using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture owns two real repositories, registry and stores; git override is execution-scoped.
public sealed class MultiRepositoryTupleLandingTests
{
    [Fact]
    public void Execute_RegistryTargetChanges_LandsAcceptedCommitFromDeclaredCheckout()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var receipt = fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome).Landable!;
        var original = fixture.Repositories.Crosswalk;
        var originalBase = original.Git("rev-parse", "main");
        var target = Path.Combine(fixture.Repositories.Home.Root, "landing-target");
        fixture.Repositories.Home.Git("clone", "--no-hardlinks", original.CheckoutPath, target);
        var branch = GoalRepositoryWorktreeLayout.BranchName(fixture.Goal.Id, "crosswalk");
        GoalRepositoryWorktreeProvisionerFixture.Run(target, "config", "user.name", "Tuple fixture");
        GoalRepositoryWorktreeProvisionerFixture.Run(target, "config", "user.email", "tuple@example.test");
        GoalRepositoryWorktreeProvisionerFixture.Run(target, "switch", "-c", branch, "origin/" + branch);
        var wrong = GoalRepositoryWorktreeProvisionerFixture.CommitChange(target, "unaccepted target branch\n");
        GoalRepositoryWorktreeProvisionerFixture.Run(target, "switch", "main");
        var registry = new OrchestratorProjectRegistry(Path.Combine(fixture.Repositories.Home.Root, "landing-registry"),
            fixture.Repositories.Registry.DataRootDirectory);
        registry.CreateProject("crosswalk", target, "main");
        var landing = new MultiRepositoryTupleLanding(fixture.Repositories.Workspace, registry);

        var result = landing.Execute(fixture.Goal, GoalLifecycleState.Verified, receipt,
            () => LandingExecutor.Execute(fixture.Repositories.Kernel, fixture.Goal, fixture.Repositories.Workspace), () => null);

        Assert.Null(result.Hold);
        Assert.NotNull(result.Record);
        Assert.Equal("fixture secondary change", GoalRepositoryWorktreeProvisionerFixture.Run(target, "show", "main:example.txt"));
        Assert.Equal(originalBase, original.Git("rev-parse", "main"));
        Assert.NotEqual(0, GitCli.Run(target, "merge-base", "--is-ancestor", wrong, "main").ExitCode);
        Assert.Equal(GoalRepositoryWorktreeProvisionerFixture.Run(target, "rev-parse", "main"), result.Record.LandedCommits["crosswalk"]);
    }

    [Fact]
    public void AdvanceOnce_PassingTuple_LandsBothRepositoriesInOrder()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var receipt = fixture.Gate.Run(fixture.Goal, GoalLifecycleState.Verified, fixture.RunHome).Landable!;
        Assert.NotNull(receipt);
        var home = fixture.Repositories.Home;
        var crosswalk = fixture.Repositories.Crosswalk;
        var homeCandidate = GoalRepositoryWorktreeProvisionerFixture.Run(fixture.HomeWorktree, "rev-parse", "HEAD");
        var otherCandidate = GoalRepositoryWorktreeProvisionerFixture.Run(fixture.SecondaryWorktree, "rev-parse", "HEAD");
        var order = new List<string>();
        using var git = LandingExecutor.UseGitRunner((root, args) =>
        {
            var result = GitCli.Run(root, args);
            if (result.Succeeded && args.Length >= 2 && args[0] == "update-ref" && args[1] == "refs/heads/main")
                order.Add(root == home.CheckoutPath ? "default" : "crosswalk");
            return result;
        });

        var driver = fixture.Driver();
        var result = driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("Landed:", Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome).Description);
        Assert.Equal(new[] { "crosswalk", "default" }, order);
        Assert.Equal("fixture secondary change", crosswalk.Git("show", "main:example.txt"));
        Assert.Equal("fixture home change", home.Git("show", "main:example.txt"));
        crosswalk.Git("merge-base", "--is-ancestor", otherCandidate, "main");
        home.Git("merge-base", "--is-ancestor", homeCandidate, "main");
        var store = new MultiRepositoryLandingRecordStore(fixture.Repositories.Workspace.OrchestratorDirectory);
        var record = store.ReadLanding(fixture.Goal.Id)!;
        Assert.NotNull(record);
        Assert.Equal(receipt.TupleCanonical, record.TupleCanonical);
        Assert.Equal(order, record.Order);
        Assert.Equal(2, record.LandedCommits.Count);
        Assert.Equal(home.Git("rev-parse", "main"), record.LandedCommits["default"]);
        Assert.Equal(crosswalk.Git("rev-parse", "main"), record.LandedCommits["crosswalk"]);
        Assert.NotEqual(homeCandidate, record.LandedCommits["default"]);
        Assert.NotEqual(otherCandidate, record.LandedCommits["crosswalk"]);
        Assert.Equal(receipt.IntegrationBases.OrderBy(pair => pair.Key), record.PreLandingTips.OrderBy(pair => pair.Key));
        Assert.Equal(2, fixture.Calls.Count); // The current receipt was consumed without another gate.
        Assert.Equal(1, fixture.LandingCalls);
        Assert.Null(store.ReadFailure(fixture.Goal.Id));
        Assert.True(GoalOperationJournal.HasCompletedLandingEvidence(GoalOperationJournal.Read(fixture.Repositories.Home.CheckoutPath, fixture.Goal.Id)));
        Assert.Equal(GoalLifecycleState.Merged, Assert.IsType<ConductorAdvanceOutcome.Executed>(
            driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative).Outcome).FromState);
        Assert.Equal(GoalLifecycleState.Recorded, Assert.IsType<ConductorAdvanceOutcome.Executed>(
            driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative).Outcome).FromState);
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
    }
}
