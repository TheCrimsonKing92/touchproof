using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// An in-scope Verified goal survives the scoped-goal filter in ConductorBatchLoop.cs:867-875
/// and the eligibility check at :888-891. ConductorWorkingSet.IsLoopEligibleGoal
/// (ConductorWorkingSet.cs:53-58) keeps it eligible because its workspace exists.
/// That keeps the empty-eligibility guard at ConductorBatchLoop.cs:904 false, so the
/// tick reaches RunParallelAcceptanceBatch at :1159 even with another goal set aside.
/// The August stall shape, where the gate goal is itself set aside or excluded by
/// ConductorBatchLoop.cs:873-874, remains outside these scenarios.
/// </summary>
// The isolated build-root override is process-wide and acceptance admission keeps shared fairness state.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsBlockedRecheckGateDispatch(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const string RecheckSleepPrefix = "BLOCKED_RECHECK_SLEEP ";
    private const string GateLaunchMarker = "TEST_GATE_LAUNCH";

    [Xunit.Fact]
    public void WatchLoop_BlockedAndVerifiedGoals_StartsGateInFirstTick()
    {
        var watch = new WatchLoop();
        var verified = watch.ScheduleGate();

        watch.Run();

        watch.AssertStartedInTick(verified, tick: 1, precedingSleeps: 0);
        Assert.Equal(new[] { 1, 2, 3 }, watch.Ticks.Select(tick => tick.Tick));
        Assert.Contains(watch.Lines, watch.IsBlockedGoalClamp);
        Assert.DoesNotContain(watch.Lines, IsRecheckSleep);
    }

    [Xunit.Fact]
    public void WatchLoop_GateScheduledAfterSetAside_StartsOnNextTick()
    {
        var watch = new WatchLoop();
        Goal? verified = null;

        watch.Run(sleeps =>
        {
            if (sleeps == 1)
                verified = watch.ScheduleGate();
        });

        Assert.NotNull(verified);
        var linesBeforeLaunch = watch.AssertStartedInTick(verified, tick: 2, precedingSleeps: 1);
        Assert.Contains(linesBeforeLaunch, line => line.StartsWith("WATCH_SLEEP tick=1 ", StringComparison.Ordinal));
        Assert.Contains(linesBeforeLaunch, watch.IsBlockedGoalClamp);
    }

    [Xunit.Fact]
    public void WatchLoop_OnlySetAsideGoal_SleepsForRecheckAndReadmits()
    {
        var watch = new WatchLoop();

        watch.Run(sleeps =>
        {
            if (sleeps == 2)
                watch.HasOpenClarification = false;
        });

        Assert.Contains(watch.Lines, line => line.StartsWith(RecheckSleepPrefix + "goals=1 ", StringComparison.Ordinal));
        Assert.Equal(1, watch.Escalations);
        Assert.Empty(watch.Launches);
        Assert.Equal(2, Assert.Single(watch.WorkspaceCreationSleepCounts));
        Assert.Contains(watch.BlockedGoal.Timeline, entry =>
            entry.Kind == ProgressKind.GoalPolicyDecision &&
            entry.Message.Contains("re-admitted escalated goal after state changed", StringComparison.Ordinal));
        Assert.Contains(watch.Ticks, tick => tick.Advanced > 0);
    }

    private static bool IsRecheckSleep(string line) => line.StartsWith(RecheckSleepPrefix, StringComparison.Ordinal);

    private sealed class WatchLoop
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        private readonly AgentOrchestratorKernel _kernel = new(new FixedClock(Now));
        private int _sleeps;
        private string _output = string.Empty;

        internal WatchLoop() => BlockedGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            _kernel, DefaultAgents(), "Goal awaiting clarification");

        internal Goal BlockedGoal { get; }
        internal bool HasOpenClarification { get; set; } = true;
        internal int Escalations { get; private set; }
        internal List<int> WorkspaceCreationSleepCounts { get; } = [];
        internal List<BatchTickSummary> Ticks { get; } = [];
        internal List<(string GoalId, int Tick, int PrecedingSleeps)> Launches { get; } = [];
        internal string[] Lines { get; private set; } = [];

        internal Goal ScheduleGate()
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                _kernel, DefaultAgents(), "Update src/GateReady.cs");
            // An eligible Verified goal schedules its first attempt through batch admission.
            PassVerificationAt(_kernel, goal, goal.Tasks.Single(), Now);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.True(_kernel.BuildVerificationGate(goal.Id).IsSatisfied);
            return goal;
        }

        internal void Run(Action<int>? afterSleep = null)
        {
            const int fakePid = 9701;
            const string buildRootVariable = DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable;
            var previousBuildRoot = Environment.GetEnvironmentVariable(buildRootVariable);
            var root = CreateTempDirectory("mcg-blocked-recheck-gate");
            try
            {
                Environment.SetEnvironmentVariable(buildRootVariable, Path.Combine(root, "build"));
                var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                    root, TrunkBranchName.Default,
                    utcNow: () => Now,
                    isProcessAlive: pid => pid == fakePid,
                    launchOwnedProcess: launch =>
                    {
                        Launches.Add((launch.Attempt.GoalId, Ticks.Count + 1, _sleeps));
                        Console.WriteLine(GateLaunchMarker);
                        return new ConductorParallelAcceptanceOwnedProcessLaunchResult(fakePid);
                    });
                var driver = MakeDriver(
                    getFacts: goal => goal.Id == BlockedGoal.Id
                        ? new GoalLifecycleFacts(
                            WorkspaceExists: WorkspaceCreationSleepCounts.Count > 0,
                            HasOpenClarification: HasOpenClarification)
                        : new GoalLifecycleFacts(WorkspaceExists: true),
                    createWorkspace: _ =>
                    {
                        WorkspaceCreationSleepCounts.Add(_sleeps);
                        return Path.Combine(root, "goal-workspace");
                    },
                    dispatchAndStart: _ => DispatchStartOutcome.Deferred("Worker dispatch held by the fixture."),
                    writeEscalation: (_, _, _) => Escalations++,
                    runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                    getLandingFileScopes: _ => ["src/GateReady.cs"],
                    parallelAcceptanceAttemptCoordinator: coordinator,
                    utcNow: () => Now,
                    executionDirectory: root);

                _output = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(utcNow: () => Now).Run(
                    _kernel,
                    driver,
                    ConductorAutonomyPolicy.Conservative,
                    Path.Combine(root, "stop"),
                    maxIterations: 5,
                    watchInterval: TimeSpan.FromSeconds(1),
                    onTick: Ticks.Add,
                    sleepFunc: _ =>
                    {
                        _sleeps++;
                        afterSleep?.Invoke(_sleeps);
                        return _sleeps == 3;
                    },
                    keepAliveWhenIdle: true));
                Lines = _output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }
            finally
            {
                Environment.SetEnvironmentVariable(buildRootVariable, previousBuildRoot);
                TryDeleteDirectory(root);
            }
        }

        internal bool IsBlockedGoalClamp(string line) =>
            line.StartsWith($"BLOCKED_RECHECK_INTERVAL_CLAMPED goal={BlockedGoal.Id.Value[..8]} ", StringComparison.Ordinal);

        internal string[] AssertStartedInTick(Goal goal, int tick, int precedingSleeps)
        {
            var tickLines = Ticks.FirstOrDefault(summary => summary.Tick == tick)?.ProgressLines ?? [];
            Assert.True(
                Launches.Count == 1 && Launches[0] == (goal.Id.Value, tick, precedingSleeps),
                $"Expected one launch for {goal.Id.Value} in tick {tick} after {precedingSleeps} sleeps. " +
                $"Observed: {string.Join(", ", Launches)}\nTick progress:\n{string.Join('\n', tickLines)}\n{_output}");
            Assert.Contains(tickLines, line =>
                line.StartsWith($"ACCEPTANCE goal={goal.Id.Value[..8]} ", StringComparison.Ordinal) &&
                line.Contains(" result=started ", StringComparison.Ordinal));
            var launchLine = Array.IndexOf(Lines, GateLaunchMarker);
            Assert.True(launchLine >= 0, _output);
            var linesBeforeLaunch = Lines[..launchLine];
            Assert.DoesNotContain(linesBeforeLaunch, IsRecheckSleep);
            return linesBeforeLaunch;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
