using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: all process state, output routing and adoption artifacts are fixture-owned.
public sealed class ProjectConductorHomeBuildRelaunchTests : ConductorBatchLoopTests
{
    public ProjectConductorHomeBuildRelaunchTests(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void FollowerOutput_OnlyIdleHandoffProvesSchemaUpgradeQuiescence(bool busy)
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var handoffs = 0;
        var text = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
            homeBuildAdoption: fixture.Detector(), countLiveGroupedGates: () => busy ? 1 : 0,
            homeBuildRelaunch: request =>
            {
                handoffs++;
                return ConductorSelfRelaunch.CreateSupervisorDelegated()(request);
            }).Run(new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
                Path.Combine(fixture.Root, "stop"), maxIterations: 1));

        Assert.Equal(busy ? 0 : 1, handoffs);
        Assert.DoesNotContain("LOOP_RELAUNCH_DRAIN ", text, StringComparison.Ordinal);
        if (busy)
        {
            Assert.Contains("blockers=gate-holders:1", text, StringComparison.Ordinal);
            Assert.DoesNotContain("LOOP_RELAUNCH_REBUILD ", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("LOOP_RELAUNCH_REBUILD ", text, StringComparison.Ordinal);
            Assert.True(text.IndexOf("LOOP_RELAUNCH_REBUILD ", StringComparison.Ordinal) <
                text.IndexOf("LOOP_HANDOFF ", StringComparison.Ordinal));
        }
        var stdoutPath = Path.Combine(fixture.Root, "child.out.log");
        File.WriteAllText(stdoutPath, text);
        Assert.Equal(busy ? "quiescence" : "", ConductorHandoffSchemaUpgrade.DrainFailure(stdoutPath));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Revalidation_AuthorityOrWorkChangesEmitNoQuiescenceProof(bool revert)
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var handoffs = 0;
        var blockers = Array.Empty<string>();
        var coordinator = new HomeBuildRelaunchCoordinator(fixture.Detector(), _ =>
        {
            handoffs++;
            throw new InvalidOperationException("A changed reservation cannot hand off");
        });
        var text = AsyncLocalConsoleRouter.Capture(() =>
        {
            var request = coordinator.TryReserve(1, () => blockers);
            Assert.NotNull(request);
            Assert.Null(coordinator.TryHandoff(request, 1, () => blockers, () =>
            {
                if (revert) fixture.Publish(fixture.Candidate with { Status = "reverted" });
                else blockers = ["gate-holders:1"];
            }));
        });

        Assert.Equal(0, handoffs);
        Assert.Contains("LOOP_RELAUNCH_SCHEDULED ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_RELAUNCH_REBUILD ", text, StringComparison.Ordinal);
        var stdoutPath = Path.Combine(fixture.Root, "child.out.log");
        File.WriteAllText(stdoutPath, text);
        Assert.Equal("quiescence", ConductorHandoffSchemaUpgrade.DrainFailure(stdoutPath));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void IdleFirstTickSchedulesExactHomeBuildAndCarriesItThroughExitArtifact(bool delegateRelaunch)
    {
        using var fixture = new HomeBuildAdoptionFixture();
        ConductorSelfRelaunchRequest? handedOff = null;
        BatchLoopSummary? summary = null;
        var text = AsyncLocalConsoleRouter.Capture(() => summary = new ConductorBatchLoop(
            homeBuildAdoption: fixture.Detector(), homeBuildRelaunch: request =>
            {
                handedOff = request;
                return ConductorSelfRelaunch.CreateSupervisorDelegated()(request);
            }).Run(new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
                Path.Combine(fixture.Root, "stop"), maxIterations: 2));
        Assert.Contains($"reason=home-build-adopted commit={fixture.Candidate.AdoptedCommit}", text, StringComparison.Ordinal);
        Assert.Equal(fixture.Candidate, handedOff!.HomeBuildTarget);
        Assert.Equal(1, handedOff.Tick);
        var artifact = ConductorContinuityExitArtifactFactory.FromLoopSummary(summary!, delegateRelaunch);
        Assert.True(artifact.RestartRequested);
        Assert.Equal(fixture.Candidate, artifact.HomeBuildTarget);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void BusyTicksHoldThenFirstFullyIdleTickRelaunchesWithoutDetach(bool worker)
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var kernel = new AgentOrchestratorKernel();
        var now = DateTimeOffset.UtcNow;
        Goal? goal = null;
        if (worker)
        {
            goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Existing worker");
            kernel.RecordTaskDispatch(goal.Id, goal.Tasks.Single().Id,
                new TaskDispatchRecord("fixture", "fixture", fixture.Root, now));
            kernel.RecordTaskProcessStarted(goal.Id, goal.Tasks.Single().Id,
                new TaskProcessRecord(123, "fixture", fixture.Root, Path.Combine(fixture.Root, "out"),
                    Path.Combine(fixture.Root, "err"), Path.Combine(fixture.Root, "exit"), now, null, null));
        }
        var gates = worker ? 0 : 1;
        var turns = 0;
        var handedOffAtTurn = 0;
        var detaches = 0;
        var text = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
            homeBuildAdoption: fixture.Detector(), countLiveGroupedGates: () => gates,
            measuredSweep: _ =>
            {
                turns++;
                if (turns == 2)
                {
                    if (goal is not null)
                    {
                        var task = goal.Tasks.Single();
                        kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
                            task.LastProcess! with { CompletedAt = now, ExitCode = 0 }, null);
                        // The worker already has a dispatch; complete that round without redispatching it.
                        kernel.RecordTaskVerification(goal.Id, task.Id,
                            new TaskVerificationRecord("fixture", fixture.Root, 0, "ok", "", now));
                    }
                    gates = 1;
                }
                if (turns == 3) gates = 0;
                return null;
            },
            reapGoalRunningDispatches: (_, _) => detaches++, detachGoalRunningDispatches: (_, _) => detaches++,
            homeBuildRelaunch: request =>
            {
                handedOffAtTurn = turns;
                return ConductorSelfRelaunch.CreateSupervisorDelegated()(request);
            }).Run(kernel, MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true)),
                ConductorAutonomyPolicy.Conservative, Path.Combine(fixture.Root, "stop"), maxIterations: 4,
                keepAliveWhenIdle: true, watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => turns >= 4));
        Assert.Equal(3, handedOffAtTurn);
        Assert.DoesNotContain("LOOP_JANITORIAL_FAILED", text, StringComparison.Ordinal);
        Assert.Contains(worker ? "blockers=running-workers:1" : "blockers=gate-holders:1", text, StringComparison.Ordinal);
        Assert.Contains("blockers=gate-holders:1", text, StringComparison.Ordinal);
        Assert.Equal(worker ? 2 : 1, CountOccurrences(text, "LOOP_RELAUNCH_HELD"));
        Assert.Equal(0, detaches);
        Assert.DoesNotContain("LOOP_RELAUNCH_DETACH", text, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void EqualOrRevertedAdoptionNeverSchedules(bool reverted)
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var record = reverted ? fixture.Candidate with { Status = "reverted" }
            : fixture.Candidate with { AdoptedCommit = fixture.Running.CommitSha };
        fixture.Publish(record);
        var calls = 0;
        var text = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
            homeBuildAdoption: fixture.Detector(), homeBuildRelaunch: _ => { calls++; throw new InvalidOperationException(); })
            .Run(new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
                Path.Combine(fixture.Root, "stop"), maxIterations: 1));
        Assert.Equal(0, calls);
        Assert.DoesNotContain("LOOP_RELAUNCH_SCHEDULED", text, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RevertBetweenReservationAndHandoffKeepsIncumbentRunning()
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var reads = 0;
        var calls = 0;
        var detector = fixture.Detector(() => ++reads == 1 ? fixture.Candidate : fixture.Candidate with { Status = "reverted" });
        BatchLoopSummary? summary = null;
        AsyncLocalConsoleRouter.Capture(() => summary = new ConductorBatchLoop(homeBuildAdoption: detector,
            homeBuildRelaunch: _ => { calls++; throw new InvalidOperationException(); })
            .Run(new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
                Path.Combine(fixture.Root, "stop"), maxIterations: 1));
        Assert.True(reads >= 2);
        Assert.Equal(0, calls);
        Assert.Null(summary!.HomeBuildTarget);
        Assert.Null(summary.Handoff);
    }
}
