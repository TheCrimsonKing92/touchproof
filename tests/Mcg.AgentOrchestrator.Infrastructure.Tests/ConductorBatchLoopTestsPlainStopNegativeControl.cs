using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsPlainStopNegativeControl(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public Task PlainStopWithInFlightGate_DetachesWithoutWaiting() =>
        VerifyPlainStopWithHeldGate(startDrainFirst: false);

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public Task PlainStopWinsOverActiveDrain() =>
        VerifyPlainStopWithHeldGate(startDrainFirst: true);

    private static async Task VerifyPlainStopWithHeldGate(bool startDrainFirst)
    {
        using var paths = new ConductorVerbStartTests.Fixture();
        var stopPath = paths.Workspace.ConductorStopFilePath;
        var drainPath = ConductorDrainSignal.PathFor(stopPath);
        var clock = new ManualConductorTimeProviderForTests(
            new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new HoldingAcceptanceAttemptsAcrossTicksFixture(clock);
        var kernel = new AgentOrchestratorKernel();
        var running = CreateVerifiedSimpleGoal(kernel, "Update src/PlainStopHeldGate.cs");
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            getLandingFileScopes: _ => ["src/PlainStopHeldGate.cs"],
            parallelAcceptanceAttemptCoordinator: fixture.AttemptCoordinator,
            getAcceptanceSlotCount: _ => 1);
        var detached = new List<GoalId>();
        var detachOnSleep = startDrainFirst ? 2 : 1;
        var sleeps = 0;
        var checkpoints = 0;
        var checkpointsBeforeStop = 0;
        string? attemptId = null;
        BatchLoopSummary? summary = null;

        var output = CaptureConsole(() => summary = new ConductorBatchLoop(
            detachGoalRunningDispatches: (_, goal) => detached.Add(goal.Id)).Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, stopPath,
            watchInterval: TimeSpan.FromSeconds(1),
            persistTick: _ => checkpoints++,
            sleepFunc: _ =>
            {
                Assert.True(++sleeps <= detachOnSleep, "Plain stop must detach without another wait.");
                Assert.Equal(1, fixture.HeldAttemptCount);
                var heldAttempt = fixture.RequiredHandleForTests(running).Attempt;
                attemptId ??= heldAttempt.AttemptId;
                Assert.Equal(attemptId, heldAttempt.AttemptId);

                if (sleeps == detachOnSleep)
                {
                    checkpointsBeforeStop = checkpoints;
                    File.WriteAllText(stopPath, "stop");
                }
                else
                {
                    ConductorDrainSignal.Write(drainPath, 1800);
                }

                return false;
            }));

        Assert.Equal(detachOnSleep, sleeps);
        Assert.True(summary!.StopRequested);
        Assert.Equal("stop-file-during-sleep", summary.StopReason);
        Assert.Equal([running.Id], detached);
        Assert.True(checkpoints > checkpointsBeforeStop);
        Assert.Equal(1, fixture.HeldAttemptCount);
        var latest = fixture.AttemptCoordinator.TryReadLatest(running.Id.Value);
        Assert.NotNull(latest);
        Assert.Equal(attemptId, latest.AttemptId);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, latest.Outcome);

        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var terminal = Assert.Single(lines, line => line.StartsWith("LOOP_STOP ", StringComparison.Ordinal));
        Assert.Contains(" reason=stop-file-during-sleep ts=", terminal, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, line => line.StartsWith("LOOP_DRAINED ", StringComparison.Ordinal));
        if (startDrainFirst)
        {
            Assert.Contains(lines, line => line.StartsWith("LOOP_DRAIN ", StringComparison.Ordinal) &&
                line.Contains($"gate:{running.Id.Value}:{attemptId}", StringComparison.Ordinal));
        }
        else
        {
            Assert.False(File.Exists(drainPath));
            Assert.DoesNotContain(lines, line => line.StartsWith("LOOP_DRAIN ", StringComparison.Ordinal));
        }
    }
}
