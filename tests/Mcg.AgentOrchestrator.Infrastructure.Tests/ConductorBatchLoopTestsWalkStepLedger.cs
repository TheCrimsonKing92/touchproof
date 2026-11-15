using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Isolated kernels, fake clocks and CPU sources; no machine-wide observations.
[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsWalkStepLedger(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void SeveralGoals_FakeClockAndCpu_ReportExactExclusiveWalkFields()
    {
        var (kernel, _) = SimpleGoal();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Second walk goal");
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Third walk goal");
        var clock = new FixtureTimeProvider();
        long cpuTicks = 0;
        var ownerThread = Environment.CurrentManagedThreadId;
        var advances = 0;
        var refreshes = 0;
        void Advance(int cpuMs, int wallMs)
        {
            Interlocked.Add(ref cpuTicks, TimeSpan.FromMilliseconds(cpuMs).Ticks);
            clock.Advance(TimeSpan.FromMilliseconds(wallMs));
        }
        var costs = new Dictionary<string, (int Cpu, int Wall)>
        {
            ["walk-advance"] = (5, 11), ["walk-projection-invalidate"] = (1, 2),
            ["walk-outcome-capacity"] = (3, 5), ["walk-disposition"] = (4, 7),
            ["walk-policy-decision"] = (6, 13)
        };
        var driver = MakeDriver(createWorkspace: _ =>
        {
            advances++;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // A dedicated thread guarantees this sample cannot inline onto the owner.
            var worker = new Thread(() =>
            {
                try
                {
                    Assert.NotEqual(ownerThread, Environment.CurrentManagedThreadId);
                    ConductorTickStepLedger.Measure("walk-reap", () => Advance(2, 3));
                    finished.SetResult();
                }
                catch (Exception exception) { finished.SetException(exception); }
            }) { IsBackground = true };
            worker.Start();
            finished.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
                .GetAwaiter().GetResult(); // Hang guard: wait for the off-thread sample.
            return "/tmp/workspace";
        });
        var loop = new ConductorBatchLoop(processCpuTime: () => TimeSpan.FromTicks(Interlocked.Read(ref cpuTicks)),
            refreshGoalDispatchesBeforeAdvance: (_, _) =>
            {
                refreshes++;
                Advance(8, 17); // Refresh is now a named exclusive sub-step.
                return null;
            }).WithDiagnosticTimeProvider(clock).WithStepProbe(name =>
            {
                if (costs.TryGetValue(name, out var cost)) Advance(cost.Cpu, cost.Wall);
            });

        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
        var line = ConductorBatchLoopTestsStepLedger.Phase(tick, "per-goal-walk");

        Assert.Equal(3, advances);
        Assert.Equal(3, refreshes);
        Assert.Equal(3, tick.Advanced);
        Assert.Equal("walk-advance:21:42:3,walk-retry-advance:0:0:0,walk-outcome-capacity:9:15:3," +
            "walk-disposition:12:21:3,walk-policy-decision:18:39:3,walk-reap:0:0:3," +
            "walk-set-aside:0:0:0,walk-projection-invalidate:6:12:6," + ZeroSelectionSteps + HoldSteps(3, 24, 51) + "walk-remainder:0:0:1",
            Field(line, "walk_steps"));
        Assert.Equal("90", Field(line, "walk_cpu_owner_ms"));
        Assert.Equal("180", Field(line, "elapsed_ms"));
        Assert.Equal("3", Field(line, "goals"));
        Assert.Matches(@"^[a-f0-9]{8}:60ms:Executed(?:\|[a-f0-9]{8}:60ms:Executed){2}$", Field(line, "slowest"));
        Assert.EndsWith(" cpu_ms=90", line);
        Assert.Equal(ConductorTickStepLedger.WalkNames.Append(ConductorTickStepLedger.WalkRemainderName),
            Field(line, "walk_steps").Split(',').Select(entry => entry.Split(':')[0]));
        var wallSum = Field(line, "walk_steps").Split(',').Sum(entry =>
            long.Parse(entry.Split(':')[2], CultureInfo.InvariantCulture));
        Assert.InRange(Math.Abs(wallSum - long.Parse(Field(line, "elapsed_ms"), CultureInfo.InvariantCulture)), 0, 5);
    }

    [Fact]
    public void EmptyWalk_ZeroFillsStepsAndAttributesWholeWindowToRemainder()
    {
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
        Assert.Equal("walk_steps=walk-advance:0:0:0,walk-retry-advance:0:0:0,walk-outcome-capacity:0:0:0," +
            "walk-disposition:0:0:0,walk-policy-decision:0:0:0,walk-reap:0:0:0,walk-set-aside:0:0:0," +
            "walk-projection-invalidate:0:0:0," + ZeroSelectionSteps + HoldSteps(0) + "walk-remainder:7:19:1 walk_cpu_owner_ms=7",
            ledger.FormatWalk(TimeSpan.FromMilliseconds(19), TimeSpan.FromMilliseconds(7)));
    }

    [Fact]
    public void NestedWalkSteps_FractionalSamples_ReconcileAfterAggregationAndReset()
    {
        var (kernel, _) = SimpleGoal();
        var clock = new FixtureTimeProvider();
        var cpu = TimeSpan.Zero;
        var loop = new ConductorBatchLoop(processCpuTime: () => cpu)
            .WithDiagnosticTimeProvider(clock).WithStepProbe(name =>
            {
                if (name != "walk-advance") return;
                for (var i = 0; i < 3; i++)
                    ConductorTickStepLedger.Measure("walk-retry-advance", () =>
                    {
                        cpu += TimeSpan.FromTicks(6000);
                        clock.Advance(TimeSpan.FromTicks(6000));
                    });
                cpu += TimeSpan.FromMilliseconds(4);
                clock.Advance(TimeSpan.FromMilliseconds(8));
            });

        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, MakeDriver());
        var line = ConductorBatchLoopTestsStepLedger.Phase(tick, "per-goal-walk");
        Assert.Equal("walk-advance:4:8:1,walk-retry-advance:1:1:3,walk-outcome-capacity:0:0:1," +
            "walk-disposition:0:0:1,walk-policy-decision:0:0:1,walk-reap:0:0:0," +
            "walk-set-aside:0:0:0,walk-projection-invalidate:0:0:2," + ZeroSelectionSteps + HoldSteps(1) + "walk-remainder:0:0:1",
            Field(line, "walk_steps"));
        Assert.Equal("5", Field(line, "walk_cpu_owner_ms"));
        Assert.Equal("9", Field(line, "elapsed_ms"));
        var fresh = new ConductorTickStepLedger(() => cpu);
        Assert.Contains("walk-retry-advance:0:0:0", fresh.FormatWalk(TimeSpan.Zero, TimeSpan.Zero));
    }

    [Fact]
    public void GuardSelection_OneWalk_ReportsOrderedPartsAndExclusiveTotals()
    {
        var root = CreateTempDirectory("walk-selection-ledger");
        try
        {
            var project = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, "DeclaredTests.cs"),
                "public class DeclaredTests { [Xunit.Fact] public void Runs() {} }");
            var names = new[]
            {
                "walk-pretester-declared-resolve", "walk-pretester-changed-file-guards", "walk-pretester-cli-surface",
                "walk-pretester-coupled-class", "walk-pretester-reverse-dependency", "walk-pretester-always-run",
                "walk-pretester-test-source"
            };
            var (kernel, _) = SimpleGoal();
            var clock = new FixtureTimeProvider();
            var cpu = TimeSpan.Zero;
            var selections = 0;
            var driver = MakeDriver(createWorkspace: _ =>
            {
                var declared = ConductorTickStepLedger.Measure(names[0],
                    () => DeveloperDeferredTestSelections.Resolve(root, "deferred - DeclaredTests"));
                Assert.Single(declared.Selections);
                PreTesterGuardSelections.Create(new(), root, [], "fixture-main-revision");
                selections++;
                return root;
            });
            var loop = new ConductorBatchLoop(processCpuTime: () => cpu)
                .WithDiagnosticTimeProvider(clock).WithStepProbe(name =>
                {
                    var index = Array.IndexOf(names, name);
                    var cost = index >= 0 ? index + 1 : name == "walk-advance" ? 8 : 0;
                    cpu += TimeSpan.FromMilliseconds(cost);
                    clock.Advance(TimeSpan.FromMilliseconds(cost * 2));
                });

            var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
            var line = ConductorBatchLoopTestsStepLedger.Phase(tick, "per-goal-walk");

            Assert.Equal(1, selections);
            Assert.Equal(1, tick.Advanced);
            Assert.Equal("walk-advance:8:16:1,walk-retry-advance:0:0:0,walk-outcome-capacity:0:0:1," +
                "walk-disposition:0:0:1,walk-policy-decision:0:0:1,walk-reap:0:0:0," +
                "walk-set-aside:0:0:0,walk-projection-invalidate:0:0:2," +
                "walk-pretester-declared-resolve:1:2:1,walk-pretester-changed-file-guards:2:4:1," +
                "walk-pretester-cli-surface:3:6:1,walk-pretester-coupled-class:4:8:1," +
                "walk-pretester-reverse-dependency:5:10:1,walk-pretester-always-run:6:12:1," +
                "walk-pretester-test-source:7:14:1," + HoldSteps(1) + "walk-remainder:0:0:1", Field(line, "walk_steps"));
            Assert.Equal(ConductorTickStepLedger.WalkNames.Append(ConductorTickStepLedger.WalkRemainderName),
                Field(line, "walk_steps").Split(',').Select(entry => entry.Split(':')[0]));
            Assert.Equal("36", Field(line, "walk_cpu_owner_ms"));
            Assert.Equal("72", Field(line, "elapsed_ms"));
            var wallSum = Field(line, "walk_steps").Split(',').Sum(entry =>
                long.Parse(entry.Split(':')[2], CultureInfo.InvariantCulture));
            Assert.InRange(Math.Abs(wallSum - long.Parse(Field(line, "elapsed_ms"), CultureInfo.InvariantCulture)), 0, 5);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private const string ZeroSelectionSteps =
        "walk-pretester-declared-resolve:0:0:0,walk-pretester-changed-file-guards:0:0:0," +
        "walk-pretester-cli-surface:0:0:0,walk-pretester-coupled-class:0:0:0," +
        "walk-pretester-reverse-dependency:0:0:0,walk-pretester-always-run:0:0:0,walk-pretester-test-source:0:0:0,";

    private static string HoldSteps(int calls, int refreshCpu = 0, int refreshWall = 0) =>
        $"walk-held-goal:0:0:{calls},walk-running-refresh:{refreshCpu}:{refreshWall}:{calls},walk-running-glance:0:0:0," +
        $"walk-hold-entry-gates:0:0:{calls},walk-hold-lifecycle-reconcile:0:0:{calls},walk-hold-lifecycle-facts:0:0:{calls}," +
        "walk-running-hold-reason:0:0:0,walk-changed-files:0:0:0,walk-candidate-context:0:0:0," +
        "walk-finding-sibling-classes:0:0:0,walk-recovery-facts:0:0:0,walk-recovery-observe-finding:0:0:0," +
        $"walk-recovery-failure-source:0:0:0,walk-recovery-evidence-attempt:0:0:0,walk-candidate-revision:0:0:0,walk-goal-overhead:0:0:{calls},";

    private static string Field(string line, string name)
    {
        var match = Regex.Match(line, @"(?:^| )" + Regex.Escape(name) + @"=(\S+)");
        Assert.True(match.Success, $"Missing {name}: {line}");
        return match.Groups[1].Value;
    }

    private sealed class FixtureTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
