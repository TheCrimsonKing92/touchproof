using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each test uses its own sweep state, clock, and filesystem roots.
public sealed class TerminalGoalSweepTempJanitorCadenceTests : IDisposable
{
    private readonly DirectoryInfo fixtureRoot = Directory.CreateTempSubdirectory("owned-root-janitor-");
    private readonly SweepClock clock = new(DateTimeOffset.UtcNow.AddDays(2));
    private readonly TerminalGoalSweep.OwnedRootSweepState sweepState = new();

    [Fact]
    public void Sweep_reports_janitor_counts_and_skips_the_tick_one_minute_later()
    {
        var old = SeedFile(CaptureName(), age: TimeSpan.FromHours(3));
        var fresh = SeedFile(CaptureName(), age: TimeSpan.FromMinutes(5));
        var unrelated = SeedFile("unrelated.out", age: TimeSpan.FromHours(3));

        var first = RunTick();

        Assert.Matches(
            @" sweep_steps=\S*,owned-root-reap:0:1,owned-root-build-roots:0:1,owned-root-temp-janitor:0:1,owned-root-orphan-fixtures:0:1,",
            first.SweepTiming);
        Assert.Equal(
            "SWEEP_OWNED_ROOT_OBSERVED detail=\"owned-root-reap roots_examined=0 roots_removed=0 " +
            "temp-artifact-janitor entries_enumerated=3 removed_files=1 removed_folders=0 bytes=3 " +
            "retained_fresh=1 retained_held=0 failed=0\"",
            first.Observation);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));

        var leaked = SeedFile(CaptureName(), age: TimeSpan.FromHours(3));
        clock.Advance(TimeSpan.FromMinutes(1));

        var skipped = RunTick();

        Assert.Contains(
            ",owned-root-reap:0:1,owned-root-build-roots:0:0,owned-root-temp-janitor:0:0,owned-root-orphan-fixtures:0:1,",
            skipped.SweepTiming);
        Assert.Equal(
            "SWEEP_OWNED_ROOT_OBSERVED detail=\"owned-root-reap roots_examined=0 roots_removed=0 build-root-reap skipped=cadence temp-artifact-janitor skipped=cadence\"",
            skipped.Observation);
        Assert.True(File.Exists(leaked));
    }

    [Fact]
    public void Sweep_reaps_a_capture_created_after_a_skip_at_the_ten_minute_boundary()
    {
        var first = RunTick();
        Assert.Contains("entries_enumerated=0 removed_files=0 removed_folders=0", first.Observation);

        clock.Advance(TimeSpan.FromMinutes(9));
        var skipped = RunTick();
        Assert.Contains("temp-artifact-janitor skipped=cadence", skipped.Observation);
        var leaked = SeedFile(CaptureName(), age: TimeSpan.FromHours(3));

        clock.Advance(TimeSpan.FromMinutes(1));
        var due = RunTick();

        Assert.Contains(",owned-root-temp-janitor:0:1,", due.SweepTiming);
        Assert.Contains("entries_enumerated=1 removed_files=1 removed_folders=0", due.Observation);
        Assert.False(File.Exists(leaked));
    }

    private (string SweepTiming, string Observation) RunTick()
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Owned-root sweep fixture");
        TerminalGoalSweepOwnedRootResult? ownedRoots = null;
        var loop = new ConductorBatchLoop(measuredSweep: _ =>
        {
            ownedRoots = ConductorTickStepLedger.Measure("owned-root-reap", () => TerminalGoalSweep.ReapOwnedBuildRoots(
                Path.Combine(fixtureRoot.FullName, "unused.db"),
                reclaimGoalRoots: false,
                tempRoot: TempRoot,
                clock: clock,
                sweepState: sweepState,
                storageRoot: new DotnetBuildStorageRoot(Path.Combine(fixtureRoot.FullName, "build-storage")),
                orchestratorTempParent: Path.Combine(fixtureRoot.FullName, "orchestrator-temp")));
            return new TerminalGoalSweepResult([]) { OwnedRoots = ownedRoots };
        }, processCpuTime: () => TimeSpan.Zero);

        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, ConductorBatchLoopTestsStepLedger.FixtureDriver());
        Assert.NotNull(ownedRoots);
        var observation = Assert.Single(ownedRoots.OperatorEvents,
            line => line.StartsWith("SWEEP_OWNED_ROOT_OBSERVED", StringComparison.Ordinal));
        return (ConductorBatchLoopTestsStepLedger.Phase(tick, "sweep"), observation);
    }

    private string TempRoot => Path.Combine(fixtureRoot.FullName, "temp");

    private string SeedFile(string name, TimeSpan age)
    {
        Directory.CreateDirectory(TempRoot);
        var path = Path.Combine(TempRoot, name);
        File.WriteAllText(path, "log");
        var written = (clock.GetUtcNow() - age).UtcDateTime;
        File.SetCreationTimeUtc(path, written);
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    private static string CaptureName() => $"mcg-acc-{Guid.NewGuid():N}.out";

    public void Dispose() => fixtureRoot.Delete(recursive: true);

    private sealed class SweepClock(DateTimeOffset start) : TimeProvider
    {
        private TimeSpan elapsed;

        public override DateTimeOffset GetUtcNow() => start + elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed.Ticks;

        internal void Advance(TimeSpan amount) => elapsed += amount;
    }
}
