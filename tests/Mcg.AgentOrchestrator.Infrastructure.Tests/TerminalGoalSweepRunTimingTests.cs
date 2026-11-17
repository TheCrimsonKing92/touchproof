using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each sweep owns its directory, kernel, stores and fake git runner.
// The flush probe is local to the test's async flow and restored in finally.
public sealed class TerminalGoalSweepRunTimingTests
{
    [Fact]
    public void Run_DelayedCacheFlush_IsIncludedInRunDuration()
    {
        using var fixture = new SweepFixture(1);
        var injectedDelay = TimeSpan.FromMilliseconds(50);
        var probeCalls = 0;
        var priorProbe = TerminalGoalSweepRunTiming.CacheFlushProbe.Value;
        TerminalGoalSweepRunTiming.CacheFlushProbe.Value = () =>
        {
            probeCalls++;
            // Establish a minimum delay at the flush seam, with no upper time bound.
            var delay = Stopwatch.StartNew();
            while (delay.Elapsed < injectedDelay)
                Thread.SpinWait(64);
        };
        try
        {
            var result = fixture.Run(new TerminalGoalSweepCache());

            Assert.Equal(1, probeCalls);
            Assert.True(File.Exists(fixture.CachePath)); // The real flush still persisted the entry.
            Assert.Single(result.GoalDurations);
            Assert.True(result.RunTiming.CacheFlush >= injectedDelay);
            Assert.True(result.RunTiming.Run >= result.RunTiming.Setup + result.RunTiming.CacheFlush +
                TimeSpan.FromMilliseconds(result.GoalsDurationMs));
            // Compare at the output's millisecond precision; the remainder retains sub-ms precision.
            Assert.Equal(result.GoalsDurationMs - result.GoalDurations.Sum(item => item.ElapsedMs),
                (long)result.RunTiming.GoalLoopRemainder.TotalMilliseconds);
        }
        finally
        {
            TerminalGoalSweepRunTiming.CacheFlushProbe.Value = priorProbe;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Run_CachePresence_IsReportedIndependentlyOfTerminalGoalCounts(int goalCount)
    {
        using var fixture = new SweepFixture(goalCount);
        var uncached = fixture.Run(cache: null);
        Assert.False(uncached.CacheConsulted);
        Assert.Equal(TimeSpan.Zero, uncached.RunTiming.CacheFlush);
        Assert.Equal(0, uncached.CacheHitCount + uncached.CacheMissCount);
        Assert.Contains(" sweep_cache_consulted=0", TerminalGoalSweepRunTiming.FormatTickDetail(uncached,
            new Dictionary<string, long>()), StringComparison.Ordinal);

        var cache = new TerminalGoalSweepCache();
        var cached = fixture.Run(cache);
        var repeated = fixture.Run(cache);
        foreach (var result in new[] { cached, repeated })
        {
            Assert.True(result.CacheConsulted);
            Assert.Equal(goalCount, result.CacheHitCount + result.CacheMissCount);
            Assert.Contains(" sweep_cache_consulted=1", TerminalGoalSweepRunTiming.FormatTickDetail(result,
                new Dictionary<string, long>()), StringComparison.Ordinal);
        }
        Assert.Equal(goalCount, cached.CacheMissCount);
        Assert.Equal(goalCount, repeated.CacheHitCount);
    }

    [Theory]
    [InlineData(100, 130, 55, 30)]
    [InlineData(10, 5, 0, 0)]
    public void FormatTickDetail_FixedIntervals_SubtractsAndClampsRemainders(
        int runMs, int sweepMs, int unattributedMs, int outsideRunMs)
    {
        var result = FixedResult(TimeSpan.FromMilliseconds(runMs));

        var detail = TerminalGoalSweepRunTiming.FormatTickDetail(result,
            new Dictionary<string, long> { ["sweep"] = sweepMs });

        Assert.Contains($" sweep_unattributed_ms={unattributedMs} sweep_outside_run_ms={outsideRunMs}",
            detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatTickDetail_FractionalIntervals_SubtractsBeforeTruncating()
    {
        var result = FixedResult(TimeSpan.FromMilliseconds(100.8)) with
        {
            RunTiming = new(TimeSpan.FromMilliseconds(100.8), TimeSpan.FromMilliseconds(8.4),
                TimeSpan.FromMilliseconds(9.4), TimeSpan.FromMilliseconds(0.8))
        };

        var detail = TerminalGoalSweepRunTiming.FormatTickDetail(result,
            new Dictionary<string, long> { ["sweep"] = 130 });

        Assert.Contains(" sweep_setup_ms=8 sweep_cache_flush_ms=9 sweep_goal_loop_remainder_ms=0 sweep_run_ms=100",
            detail, StringComparison.Ordinal);
        Assert.Contains(" sweep_unattributed_ms=55 sweep_outside_run_ms=29", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatTickDetail_NullResult_ReportsZeroEvenWithDelegateTime()
    {
        Assert.Equal(" sweep_owned_root_ms=0 sweep_setup_ms=0 sweep_cache_flush_ms=0 sweep_goal_loop_remainder_ms=0 sweep_run_ms=0 sweep_slowest=none sweep_unattributed_ms=0 sweep_outside_run_ms=0 sweep_cache_consulted=0",
            TerminalGoalSweepRunTiming.FormatTickDetail(null, new Dictionary<string, long> { ["sweep"] = 500 }));
    }

    private static TerminalGoalSweepResult FixedResult(TimeSpan run) => new([])
    {
        OwnedRootDurationMs = 1, GitIndexDurationMs = 2, EvidenceDurationMs = 3,
        EphemeralDurationMs = 4, AttentionDurationMs = 5, MergeEvidenceDurationMs = 6,
        GoalsDurationMs = 7,
        // Deliberately unrelated to goals_ms: these are already contained in that interval.
        GoalDurations = [(new("12345678"), 700, 2)],
        RunTiming = new(run, TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(9),
            TimeSpan.FromMilliseconds(600))
    };

    private sealed class SweepFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-sweep-run-timing-{Guid.NewGuid():N}");
        private readonly AgentOrchestratorKernel _kernel;
        private readonly Func<string, IReadOnlyList<string>, GitCli.GitResult> _runner;
        private int _gitCalls;
        internal string CachePath => Path.Combine(_root, ".orchestrator", "terminal-goal-sweep-cache.json");

        internal SweepFixture(int goalCount)
        {
            Directory.CreateDirectory(_root);
            var kernel = new AgentOrchestratorKernel();
            for (var index = 0; index < goalCount; index++)
                kernel.CreateGoal($"Timing goal {index}", [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
            var snapshot = kernel.ExportSnapshot();
            _kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with
                {
                    Status = GoalStatus.Completed,
                    Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray()
                }).ToArray()
            });
            _runner = RunGit;
        }

        internal TerminalGoalSweepResult Run(TerminalGoalSweepCache? cache)
        {
            var before = _gitCalls;
            var result = TerminalGoalSweep.Run(_kernel, _root, TrunkBranchName.Default, cache: cache, gitRunner: _runner);
            Assert.True(_gitCalls > before);
            Assert.Equal(_gitCalls - before, result.GitSpawnCount);
            Assert.All(_kernel.Goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            return result;
        }

        private GitCli.GitResult RunGit(string directory, IReadOnlyList<string> arguments)
        {
            Assert.Equal(_root, directory);
            _gitCalls++;
            var command = string.Join(" ", arguments);
            return command switch
            {
                "for-each-ref --format=%(refname:lstrip=2) %(objectname) refs/heads/goal/ refs/heads/main" =>
                    new(0, $"main {new string('1', 40)}\n", string.Empty),
                "for-each-ref --format=%(refname:short) --merged HEAD refs/heads/goal/" or
                "worktree list --porcelain" => new(0, string.Empty, string.Empty),
                "rev-parse --verify refs/heads/main" => new(0, $"{new string('1', 40)}\n", string.Empty),
                _ when command == $"log {new string('1', 40)} --format=%H%x09%s --grep=^Integrate goal/" =>
                    new(0, string.Empty, string.Empty),
                _ => new(1, string.Empty, $"No such ref in timing fixture: {command}")
            };
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
