using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class TerminalGoalSweepBuildRootCadenceTests : IDisposable
{
    private readonly DirectoryInfo fixture = Directory.CreateTempSubdirectory("build-root-cadence-");
    private readonly TerminalGoalSweep.OwnedRootSweepState state = new();
    private readonly SweepClock clock = new();

    [Fact]
    public void Sweep_runs_at_the_boundary_and_early_once_for_coalesced_transitions()
    {
        var calls = 0;
        TerminalGoalSweepOwnedRootResult Reap()
        {
            calls++;
            return new(1, 0, 1, 0, []);
        }

        var first = new GoalId("11111111000000000000000000000000");
        var second = new GoalId("22222222000000000000000000000000");
        (GoalId, bool)[] active = [(first, false), (second, false)];
        (GoalId, bool)[] terminal = [(first, true), (second, true)];
        Assert.Equal(1, Sweep(Reap, active).ProcessedCount);
        clock.Advance(TimeSpan.FromMinutes(1));
        var skipped = Sweep(Reap, active);
        Assert.Equal(0, skipped.ProcessedCount);
        Assert.Empty(skipped.ReclaimedGoalRoots!);
        Assert.Empty(skipped.GoalRootReclaimFailures!);
        clock.Advance(TimeSpan.FromSeconds(239));
        Sweep(Reap, active);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Sweep(Reap, active);
        Assert.Equal(2, calls);

        clock.Advance(TimeSpan.FromMinutes(1));
        Sweep(Reap, terminal);
        Assert.Equal(3, calls);
        clock.Advance(TimeSpan.FromMinutes(4));
        Sweep(Reap, terminal);
        Assert.Equal(3, calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        Sweep(Reap, terminal);
        Assert.Equal(4, calls);

        Sweep(Reap, terminal, reclaimGoalRoots: true);
        Assert.Equal(5, calls);
    }

    [Fact]
    public void Sweep_measures_cpu_once_on_admission_and_records_zero_calls_on_skip()
    {
        var cpu = TimeSpan.Zero;
        TerminalGoalSweepOwnedRootResult Reap()
        {
            cpu += TimeSpan.FromMilliseconds(7);
            return new(1, 0, 1, 0, []);
        }

        string Tick()
        {
            var kernel = new AgentOrchestratorKernel();
            GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Build-root sweep fixture");
            var loop = new ConductorBatchLoop(measuredSweep: _ =>
            {
                var result = ConductorTickStepLedger.Measure("owned-root-reap", () => Sweep(Reap));
                return new TerminalGoalSweepResult([]) { OwnedRoots = result };
            }, processCpuTime: () => cpu);
            var tick = ConductorBatchLoopTestsStepLedger.OneTick(
                loop, kernel, ConductorBatchLoopTestsStepLedger.FixtureDriver());
            return ConductorBatchLoopTestsStepLedger.Phase(tick, "sweep");
        }

        Assert.Contains(",owned-root-build-roots:7:1,", Tick());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains(",owned-root-build-roots:0:0,", Tick());
    }

    [Fact]
    public void Sweep_counts_failed_attempts_toward_the_configured_interval()
    {
        var expected = new InvalidOperationException("build-root fixture failure");
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() =>
            Sweep(() => throw expected, intervalSeconds: 30)));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, Sweep(() => throw expected, intervalSeconds: 30).ProcessedCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Sweep(() => new(1, 0, 0, 0, []), intervalSeconds: 30).ProcessedCount);
    }

    [Fact]
    public void Policy_round_trips_the_interval_and_rejects_zero()
    {
        var policy = ConductorAutonomyPolicy.Conservative with { BuildRootReapIntervalSeconds = 30 };
        var json = policy.ToJson();
        Assert.Equal(30, ConductorAutonomyPolicy.ParseJson(json).BuildRootReapIntervalSeconds);
        Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(
            (policy with { BuildRootReapIntervalSeconds = 0 }).ToJson()));
    }

    private TerminalGoalSweepOwnedRootResult Sweep(Func<TerminalGoalSweepOwnedRootResult> reap,
        IEnumerable<(GoalId, bool)>? goals = null, bool reclaimGoalRoots = false, int intervalSeconds = 300) =>
        TerminalGoalSweep.ReapOwnedBuildRoots(Path.Combine(fixture.FullName, "unused.db"), reclaimGoalRoots,
            tempRoot: Path.Combine(fixture.FullName, "temp"), clock: clock, sweepState: state,
            storageRoot: new DotnetBuildStorageRoot(Path.Combine(fixture.FullName, "storage")),
            orchestratorTempParent: Path.Combine(fixture.FullName, "orchestrator-temp"),
            goals: goals, buildRootReapIntervalSeconds: intervalSeconds, reapCore: reap);

    public void Dispose() => fixture.Delete(recursive: true);

    internal sealed class SweepClock : TimeProvider
    {
        private TimeSpan elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed.Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) + elapsed;
        internal void Advance(TimeSpan amount) => elapsed += amount;
    }
}
