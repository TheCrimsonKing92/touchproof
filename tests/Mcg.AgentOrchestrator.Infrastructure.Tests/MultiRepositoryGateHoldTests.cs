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
        using var fixture = new GoalRepositoriesTestFixture();
        var goal = fixture.CreateGoal();
        ConductorDriverTests.PassVerification(fixture.Kernel, goal, goal.Tasks.Single());
        Assert.True(CliCommandHandlers.Execute([command, goal.Id.Value], fixture.Context()));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Contains(MultiRepositoryGateHold.Kind, goal.LandingBlocker!.Kind);
        Assert.Equal("operator", goal.LandingBlocker.NextActor);
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts")));
    }

    [Fact]
    public async Task AdvanceOnce_MultipleRepositories_HoldsAcrossReloadWithoutGateOrLanding()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var goal = fixture.CreateGoal();
        ConductorDriverTests.PassVerification(fixture.Kernel, goal, goal.Tasks.Single());
        Assert.Equal(GoalStatus.Verified, goal.Status);
        var gateCalls = 0;
        var landingCalls = 0;
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(fixture.Home.Root, "attempts"), "main", runInline: true);
        var driver = new ConductorDriver(_ => GoalLifecycleFacts.None, () => 0, _ => fixture.Home.Root,
            _ => DispatchStartOutcome.Started(), null, null,
            _ => { gateCalls++; return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
            null, null, null, (_, _, _) => 0, null,
            _ => throw new InvalidOperationException("A held goal must not rebase"),
            (_, _) => { landingCalls++; throw new InvalidOperationException("A held goal must not land"); },
            null, _ => { }, _ => throw new InvalidOperationException("A held goal must not clean up"),
            (_, _, _) => { }, _ => null, parallelAcceptanceAttemptCoordinator: coordinator);
        ConductorGateAttention.Configure(driver, fixture.Workspace.OrchestratorDirectory, _ => null);
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);

        for (var round = 0; round < 2; round++)
        {
            var kernel = round == 0 ? fixture.Kernel : await repository.LoadAsync();
            var current = kernel.GetGoal(goal.Id);
            var result = driver.AdvanceOnce(current, ConductorAutonomyPolicy.Conservative);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(MultiRepositoryGateHold.Kind, held.Decision!.DiscriminatingEvidence);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Null(driver.TryBuildParallelAcceptanceCandidate(current,
                ConductorAutonomyPolicy.Conservative, 0, out var reason));
            Assert.Equal(MultiRepositoryGateHold.Kind, reason);
            Assert.Equal(MultiRepositoryGateHold.Kind, Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
                driver.ProjectGateReadyCandidate(current, ConductorAutonomyPolicy.Conservative)).HoldReason);
            Assert.Equal(MultiRepositoryGateHold.Kind, Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
                driver.ProjectCachedGateReadyCandidate(current, ConductorAutonomyPolicy.Conservative)).HoldReason);
            Assert.Equal(MultiRepositoryGateHold.Kind, Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(
                driver.ProjectInReviewCohortPartner(current, ConductorAutonomyPolicy.Conservative)).HoldReason);
            ConductorLandingBlockerRecorder.Observe(kernel, current, held,
                DateTimeOffset.Parse("2026-11-07T15:00:00Z"), []);
            Assert.Equal("operator", current.LandingBlocker!.NextActor);
            Assert.Equal("wait-for-multi-repository-gate-slice", current.LandingBlocker.NextVerb);
            ConductorGateAttention.ReconcileOutcome(kernel, driver, current, held);
            Assert.False(coordinator.HasLiveAttempt(current.Id.Value));
            await repository.SaveAsync(kernel);
        }

        Assert.Equal(0, gateCalls);
        Assert.Equal(0, landingCalls);
        var persisted = (await repository.LoadAsync()).GetGoal(goal.Id);
        var attention = await CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory).GetAttentionQueueAsync();
        var item = Assert.Single(attention, item => item.CorrelationKey == MultiRepositoryGateHoldAttention.Prefix + goal.Id.Value);
        Assert.Contains("operator", item.Body);
        Assert.Contains(MultiRepositoryGateHold.NextAction, item.Body);
        using var output = new StringWriter();
        CliConductorStatusReader.Print(new(null, new("unavailable", null, null), null,
            new("no-build", null, null), null,
            new(true, [new(goal.Id.Value, "Verified", "none", persisted.LandingBlocker)], 0, 0),
            null, null, new("unavailable", null, [])), output);
        Assert.Contains(MultiRepositoryGateHold.Kind, output.ToString());
        Assert.Contains("next=operator", output.ToString());
    }
}
