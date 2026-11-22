using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsDrainStop(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    private const int CrossTickHangGuardMilliseconds = 30_000;

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task DrainWithInFlightGate_WaitsForReceiptThenEmitsLoopDrained()
    {
        using var paths = new ConductorVerbStartTests.Fixture();
        var stopPath = paths.Workspace.ConductorStopFilePath;
        var drainPath = ConductorDrainSignal.PathFor(stopPath);
        var eventPath = Path.Combine(paths.Workspace.ExecutionDirectory, "conduct-events.log");
        var clock = new DrainClock();
        await using var fixture = new HoldingAcceptanceAttemptsAcrossTicksFixture(NewUtcClock());
        var kernel = new AgentOrchestratorKernel();
        var running = CreateVerifiedSimpleGoal(kernel, "Update src/DrainRunningGate.cs");
        var sleeps = 0;
        string? attemptId = null;
        BatchLoopSummary? summary = null;
        var text = CaptureConsole(() => summary = new ConductorBatchLoop(
            conductEventLogWriter: new ConductEventLogWriter(eventPath), drainClock: clock).Run(
            kernel, AcceptanceDriver(fixture), ConductorAutonomyPolicy.Conservative, stopPath,
            watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ =>
            {
                var handle = fixture.RequiredHandleForTests(running);
                switch (++sleeps)
                {
                    case 1:
                        attemptId = handle.Attempt.AttemptId;
                        ConductorDrainSignal.Write(drainPath, 1);
                        break;
                    case 2:
                        Assert.Equal(1, fixture.HeldAttemptCount);
                        Assert.DoesNotContain("LOOP_DRAINED", File.ReadAllText(eventPath));
                        handle.CompleteForTests();
                        clock.Timestamp = TimeSpan.TicksPerSecond;
                        break;
                    default:
                        throw new InvalidOperationException("Expected the drain to finish after the gate publishes its result.");
                }
                return false;
            }));

        Assert.Equal(2, sleeps);
        Assert.True(summary!.StopRequested);
        Assert.Equal("drained", summary.StopReason);
        Assert.True(File.Exists(drainPath));
        Assert.Empty(fixture.AttemptCoordinator.GetUnreconciledAttempts([running.Id.Value]));
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var receipt = Assert.Single(lines, line =>
            line.StartsWith($"ACCEPTANCE goal={running.Id.Value[..8]} ", StringComparison.Ordinal) &&
            line.Contains("result=passed", StringComparison.Ordinal) &&
            line.Contains($"attempt={attemptId}", StringComparison.Ordinal));
        var drained = Assert.Single(lines, line => line.StartsWith("LOOP_DRAINED ", StringComparison.Ordinal));
        Assert.Contains($"waited=1 identities=gate:{running.Id.Value}:{attemptId} ", drained);
        Assert.True(Array.IndexOf(lines, receipt) < Array.IndexOf(lines, drained));
        Assert.Contains("loop-drained", File.ReadAllText(eventPath));
    }

    [Xunit.Fact]
    public void DrainWithNothingInFlight_EmitsLoopDrainedWithZero()
    {
        using var paths = new ConductorVerbStartTests.Fixture();
        var stopPath = paths.Workspace.ConductorStopFilePath;
        ConductorDrainSignal.Write(ConductorDrainSignal.PathFor(stopPath), 1800);
        BatchLoopSummary? summary = null;
        var text = CaptureConsole(() => summary = new ConductorBatchLoop().Run(
            new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative, stopPath,
            sleepFunc: _ => throw new InvalidOperationException("An empty drain must finish before sleeping.")));

        Assert.True(summary!.StopRequested);
        Assert.Equal("drained", summary.StopReason);
        var drained = Assert.Single(text.Split(Environment.NewLine), line =>
            line.StartsWith("LOOP_DRAINED ", StringComparison.Ordinal));
        Assert.Contains("waited=0 identities= elapsedMs=", drained);
    }

    [Xunit.Fact(Timeout = CrossTickHangGuardMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public async Task DrainObserved_ReadyGoals_CreateNoDispatchGateOrLease()
    {
        using var paths = new ConductorVerbStartTests.Fixture();
        var stopPath = paths.Workspace.ConductorStopFilePath;
        var drainPath = ConductorDrainSignal.PathFor(stopPath);
        await using var fixture = new HoldingAcceptanceAttemptsAcrossTicksFixture(NewUtcClock());
        var kernel = new AgentOrchestratorKernel();
        var running = CreateVerifiedSimpleGoal(kernel, "Update src/HoldDrainOpen.cs");
        var starts = new List<GoalId>();
        var serialGates = new List<GoalId>();
        var leaseRequests = new List<GoalId>();
        var sweepAdmission = new List<bool>();
        var driver = AcceptanceDriver(fixture, starts, serialGates, leaseRequests);
        Goal? readyGate = null;
        Goal? readyWorker = null;
        var sleeps = 0;
        BatchLoopSummary? summary = null;
        var text = CaptureConsole(() => summary = new ConductorBatchLoop(
            drainClock: new DrainClock(),
            measuredSweepWithCheckpointHolds: (_, _, admitRemedies) =>
            {
                sweepAdmission.Add(admitRemedies);
                return new TerminalGoalSweepResult([]);
            }).Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, stopPath,
            watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ =>
            {
                switch (++sleeps)
                {
                    case 1:
                        Assert.Equal(1, fixture.HeldAttemptCount);
                        ConductorDrainSignal.Write(drainPath, 60);
                        break;
                    case 2:
                        // The previous tick observed the request and waited on the gate.
                        File.Delete(drainPath);
                        readyGate = CreateVerifiedSimpleGoal(kernel, "Update src/ReadyAfterDrain.cs");
                        readyWorker = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                            kernel, DefaultAgents(), "Dispatch a paid worker after drain");
                        break;
                    case 3:
                        fixture.RequiredHandleForTests(running).CompleteForTests();
                        break;
                    default:
                        throw new InvalidOperationException("Expected the drain to finish after the only admitted gate completes.");
                }
                return false;
            }));

        Assert.Equal("drained", summary!.StopReason);
        Assert.NotNull(readyGate);
        Assert.NotNull(readyWorker);
        Assert.Equal([true, false, false, false], sweepAdmission);
        Assert.Empty(starts);
        Assert.Empty(serialGates);
        Assert.Empty(leaseRequests);
        Assert.Null(fixture.AttemptCoordinator.TryReadLatest(readyGate.Id.Value));
        Assert.All(kernel.GetGoal(readyWorker.Id).Tasks, task => Assert.Null(task.LastDispatch));
        Assert.Contains("waited=1", text);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task DrainTimeoutWithInFlightGate_TakesDetachPathNamingStillRunning()
    {
        using var paths = new ConductorVerbStartTests.Fixture();
        var stopPath = paths.Workspace.ConductorStopFilePath;
        var drainPath = ConductorDrainSignal.PathFor(stopPath);
        var clock = new DrainClock();
        await using var fixture = new HoldingAcceptanceAttemptsAcrossTicksFixture(NewUtcClock());
        var kernel = new AgentOrchestratorKernel();
        var running = CreateVerifiedSimpleGoal(kernel, "Update src/DrainTimeout.cs");
        var detached = new List<GoalId>();
        var checkpoints = 0;
        var checkpointsBeforeTimeout = 0;
        var sleeps = 0;
        string? attemptId = null;
        BatchLoopSummary? summary = null;
        var text = CaptureConsole(() => summary = new ConductorBatchLoop(
            drainClock: clock, detachGoalRunningDispatches: (_, goal) => detached.Add(goal.Id)).Run(
            kernel, AcceptanceDriver(fixture), ConductorAutonomyPolicy.Conservative, stopPath,
            watchInterval: TimeSpan.FromSeconds(30),
            persistTick: _ => checkpoints++,
            sleepFunc: interval =>
            {
                switch (++sleeps)
                {
                    case 1:
                        attemptId = fixture.RequiredHandleForTests(running).Attempt.AttemptId;
                        ConductorDrainSignal.Write(drainPath, 2);
                        break;
                    case 2:
                        Assert.Equal(TimeSpan.FromSeconds(2), interval);
                        checkpointsBeforeTimeout = checkpoints;
                        clock.Timestamp = TimeSpan.FromSeconds(2).Ticks;
                        break;
                    default:
                        throw new InvalidOperationException("Expected the expired drain to detach.");
                }
                return false;
            }));

        Assert.True(summary!.StopRequested);
        Assert.Equal("drain-timeout", summary.StopReason);
        Assert.Equal([running.Id], detached);
        Assert.True(checkpoints > checkpointsBeforeTimeout);
        Assert.Equal(1, fixture.HeldAttemptCount);
        Assert.DoesNotContain("LOOP_DRAINED ", text);
        var terminal = Assert.Single(text.Split(Environment.NewLine), line =>
            line.StartsWith("LOOP_STOP ", StringComparison.Ordinal));
        Assert.Contains("reason=drain-timeout outcome=timeout fallback=detach stillRunning=1", terminal);
        Assert.Contains($"identities=gate:{running.Id.Value}:{attemptId} ", terminal);
    }

    private static ManualConductorTimeProviderForTests NewUtcClock() =>
        new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

    private static ConductorDriver AcceptanceDriver(
        HoldingAcceptanceAttemptsAcrossTicksFixture fixture,
        List<GoalId>? starts = null,
        List<GoalId>? serialGates = null,
        List<GoalId>? leaseRequests = null) =>
        MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                starts?.Add(goal.Id);
                return DispatchStartOutcome.Started();
            },
            runAcceptanceWithSlot: (goal, _) =>
            {
                serialGates?.Add(goal.Id);
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            getLandingFileScopes: goal => [$"src/{goal.Id.Value}.cs"],
            parallelAcceptanceAttemptCoordinator: fixture.AttemptCoordinator,
            getAcceptanceSlotCount: _ => 1,
            tryAcquireEvidenceMutationLease: leaseRequests is null ? null : (goal, _) =>
            {
                leaseRequests.Add(goal.Id);
                return new TestLease();
            });

    private sealed class TestLease : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class DrainClock : TimeProvider
    {
        internal long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
