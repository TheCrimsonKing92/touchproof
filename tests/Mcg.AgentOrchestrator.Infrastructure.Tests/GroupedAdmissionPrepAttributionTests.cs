using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory goals and scoped clocks only; no process or shared store.
public sealed class GroupedAdmissionPrepAttributionTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    private static readonly string[] ExpectedNames =
    [
        "prep-cohort-receipts", "prep-train-reconcile", "prep-projection", "prep-census", "prep-plan-receipt",
        "prep-cohort-members", "prep-obligation-filter", "prep-merge-conflict", "prep-approval-hold",
        "prep-train-receipts", "prep-oldest-waiter", "prep-retry-priority",
        "prep-interaction-keys", "prep-probe-backoff"
    ];

    [Theory]
    [InlineData("prep-cohort-receipts")]
    [InlineData("prep-train-reconcile")]
    [InlineData("prep-projection")]
    [InlineData("prep-census")]
    [InlineData("prep-plan-receipt")]
    [InlineData("prep-cohort-members")]
    [InlineData("prep-obligation-filter")]
    [InlineData("prep-merge-conflict")]
    [InlineData("prep-approval-hold")]
    [InlineData("prep-train-receipts")]
    [InlineData("prep-oldest-waiter")]
    [InlineData("prep-retry-priority")]
    [InlineData("prep-interaction-keys")]
    [InlineData("prep-probe-backoff")]
    public void Substep_ThreeSecondWait_ReportsWallWithoutCpu(string step)
    {
        var clock = new FixtureTimeProvider();
        var (kernel, _) = SimpleGoal();
        // Enable retry-priority observation without starting a gate for this unverified goal.
        var driver = MakeDriver(runAcceptanceCohort: (_, _, _) =>
            throw new InvalidOperationException("An unverified fixture must not run a cohort gate."));
        Assert.True(driver.AcceptanceCohortsEnabled);
        var line = RunFixture(clock, kernel, driver, name =>
        {
            if (name == step) clock.Advance(TimeSpan.FromSeconds(3));
        });

        var children = Steps(line, "prep_steps");
        Assert.Equal((3000L, 0L, 1L), children[step]);
        Assert.Equal((3000L, 0L, 1L), Steps(line, "admission_steps")["grouped-admission-prep"]);
        Assert.All(children.Where(pair => pair.Key != step), pair => Assert.Equal(0, pair.Value.Wall));
        Assert.InRange(Field(line, "prep_unattributed_ms"), 0, 99);
        Assert.Equal(3000, Field(line, "elapsed_ms"));
        AssertPhaseReconciles(line);
    }

    [Fact]
    public void AdmissionLine_AllPrepNames_ReportsSkippedStepsAsZero()
    {
        var (kernel, _) = SimpleGoal();
        var line = RunFixture(new FixtureTimeProvider(), kernel, MakeDriver());
        var steps = Steps(line, "prep_steps");

        Assert.Equal(ExpectedNames, ConductorTickStepLedger.PrepNames);
        Assert.Equal(ExpectedNames, steps.Keys);
        Assert.All(ExpectedNames, name => Assert.Matches($@"(?:=|,){name}:\d+:\d+:\d+(?:,| )", line));
        Assert.Equal((0L, 0L, 0L), steps["prep-retry-priority"]);
        Assert.All(steps.Where(pair => pair.Key != "prep-retry-priority"),
            pair => Assert.Equal((0L, 0L, 1L), pair.Value));
        Assert.Equal(0, Field(line, "prep_projection_count"));
        Assert.Contains(" prep_projection_slowest=none:0 ", line);
        Assert.Equal(0, Field(line, "prep_unattributed_ms"));
        AssertPhaseReconciles(line);
    }

    [Fact]
    public void Projection_TwoVerifiedGoals_ReportsSlowestActualProjection()
    {
        var clock = new FixtureTimeProvider();
        var kernel = new AgentOrchestratorKernel();
        var fast = CreateVerifiedSimpleGoal(kernel, "Fast projection");
        var slow = CreateVerifiedSimpleGoal(kernel, "Slow projection");
        var projecting = false;
        var calls = new List<GoalId>();
        var driver = MakeDriver(
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            isVerificationGateSatisfied: goal =>
        {
            if (projecting)
            {
                calls.Add(goal.Id);
                clock.Advance(TimeSpan.FromMilliseconds(goal.Id == slow.Id ? 40 : 5));
            }
            return true;
        });
        Assert.True(driver.ParallelAcceptanceEnabled);
        var line = RunFixture(clock, kernel, driver, name =>
        {
            if (name == "prep-train-reconcile") projecting = true;
            if (name == "prep-projection") projecting = false;
        });

        Assert.False(projecting);
        Assert.Equal(2, calls.Count);
        Assert.Contains(fast.Id, calls);
        Assert.Contains(slow.Id, calls);
        Assert.Equal(2, Field(line, "prep_projection_count"));
        Assert.Contains($" prep_projection_slowest={slow.Id.Value[..8]}:40 ", line);
        Assert.Equal((45L, 0L, 1L), Steps(line, "prep_steps")["prep-projection"]);
        Assert.Equal((45L, 0L, 1L), Steps(line, "admission_steps")["grouped-admission-prep"]);
        Assert.Equal(0, Field(line, "prep_unattributed_ms"));
        AssertPhaseReconciles(line);
    }

    [Theory]
    [InlineData(6000, 6000, false)]
    [InlineData(6001, 6000, true)]
    public void Projection_TiesAndFractionalDurations_CompareBeforeRounding(
        long firstTicks, long secondTicks, bool firstIsSlower)
    {
        var clock = new FixtureTimeProvider();
        var kernel = new AgentOrchestratorKernel();
        var goals = new[] { CreateVerifiedSimpleGoal(kernel, "A"), CreateVerifiedSimpleGoal(kernel, "B") }
            .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal).ToArray();
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
        AtTickClock(clock, () =>
        {
            using (ledger.Activate())
            {
                // Higher ID goes first: only an exact tie should select the second.
                var observed = ConductorTickStepLedger.ObserveProjections(goals.Reverse()).Select(goal =>
                {
                    clock.Advance(TimeSpan.FromTicks(goal.Id == goals[1].Id ? firstTicks : secondTicks));
                    return goal.Id;
                }).ToArray();
                Assert.Equal(goals.Reverse().Select(goal => goal.Id), observed);
            }
        });

        var detail = ledger.FormatAdmission(TimeSpan.Zero);
        Assert.Equal(2, Field(detail, "prep_projection_count"));
        var expected = firstIsSlower ? goals[1] : goals[0];
        Assert.Contains($" prep_projection_slowest={expected.Id.Value[..8]}:0 ", detail);
    }

    [Fact]
    public void Projection_RepeatedPreparation_AccumulatesWithinOneLedgerOnly()
    {
        var clock = new FixtureTimeProvider();
        var (_, goal) = SimpleGoal();
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
        AtTickClock(clock, () =>
        {
            using (ledger.Activate())
                foreach (var delay in new[] { 20, 5 })
                    ConductorTickStepLedger.Measure("grouped-admission-prep", () =>
                        ConductorTickStepLedger.Measure("prep-projection", () =>
                        {
                            foreach (var candidate in ConductorTickStepLedger.ObserveProjections(new[] { goal }))
                            {
                                Assert.Same(goal, candidate);
                                clock.Advance(TimeSpan.FromMilliseconds(delay));
                            }
                        }));
        });

        var detail = ledger.FormatAdmission(TimeSpan.FromMilliseconds(25));
        Assert.Equal(2, Field(detail, "prep_projection_count"));
        Assert.Contains($" prep_projection_slowest={goal.Id.Value[..8]}:20 ", detail);
        Assert.Equal((25L, 0L, 2L), Steps(detail, "prep_steps")["prep-projection"]);
        Assert.Equal(0, Field(detail, "prep_unattributed_ms"));
        var nextTick = new ConductorTickStepLedger(() => TimeSpan.Zero).FormatAdmission(TimeSpan.Zero);
        Assert.Equal(0, Field(nextTick, "prep_projection_count"));
        Assert.Contains(" prep_projection_slowest=none:0 ", nextTick);
    }

    [Fact]
    public void Prep_RepeatedMeasurements_AccumulateInclusiveTotalsAndCounts()
    {
        var clock = new FixtureTimeProvider();
        var cpu = TimeSpan.Zero;
        var ledger = new ConductorTickStepLedger(() => cpu);
        AtTickClock(clock, () =>
        {
            using (ledger.Activate())
                for (var i = 0; i < 2; i++)
                    ConductorTickStepLedger.Measure("grouped-admission-prep", () =>
                    {
                        ConductorTickStepLedger.Measure("prep-census", () =>
                        {
                            clock.Advance(TimeSpan.FromMilliseconds(7));
                            cpu += TimeSpan.FromMilliseconds(2);
                        });
                        clock.Advance(TimeSpan.FromMilliseconds(3));
                        cpu += TimeSpan.FromMilliseconds(1);
                    });
        });

        var detail = ledger.FormatAdmission(TimeSpan.FromMilliseconds(20));
        Assert.Equal((20L, 6L, 2L), Steps(detail, "admission_steps")["grouped-admission-prep"]);
        Assert.Equal((14L, 4L, 2L), Steps(detail, "prep_steps")["prep-census"]);
        Assert.Equal(6, Field(detail, "prep_unattributed_ms"));
        Assert.Equal(0, Field(detail, "unattributed_ms"));
    }

    [Fact]
    public void Prep_FractionalChildren_SubtractsBeforeRounding()
    {
        var clock = new FixtureTimeProvider();
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
        AtTickClock(clock, () =>
        {
            using (ledger.Activate())
                ConductorTickStepLedger.Measure("grouped-admission-prep", () =>
                {
                    ConductorTickStepLedger.Measure("prep-census", () => clock.Advance(TimeSpan.FromTicks(6000)));
                    ConductorTickStepLedger.Measure("prep-cohort-members", () => clock.Advance(TimeSpan.FromTicks(6000)));
                });
        });

        var detail = ledger.FormatAdmission(TimeSpan.FromTicks(12000));
        Assert.Equal(1, Steps(detail, "admission_steps")["grouped-admission-prep"].Wall);
        Assert.Equal(0, Steps(detail, "prep_steps").Values.Sum(step => step.Wall));
        Assert.Equal(0, Field(detail, "prep_unattributed_ms"));
    }

    [Fact]
    public void Prep_ChildrenExceedParent_RemainderIsFlooredAtZero()
    {
        var clock = new FixtureTimeProvider();
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
        AtTickClock(clock, () =>
        {
            using (ledger.Activate())
                ConductorTickStepLedger.Measure("prep-census", () => clock.Advance(TimeSpan.FromMilliseconds(10)));
        });

        var detail = ledger.FormatAdmission(TimeSpan.Zero);
        Assert.Equal((10L, 0L, 1L), Steps(detail, "prep_steps")["prep-census"]);
        Assert.Equal((0L, 0L, 0L), Steps(detail, "admission_steps")["grouped-admission-prep"]);
        Assert.Equal(0, Field(detail, "prep_unattributed_ms"));
    }

    [Fact]
    public void Projection_FailedConsumer_RecordsTimeAndPreservesException()
    {
        var clock = new FixtureTimeProvider();
        var (_, goal) = SimpleGoal();
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
        var failure = new InvalidOperationException("projection failed");
        AtTickClock(clock, () =>
        {
            using (ledger.Activate())
            {
                var thrown = Assert.Throws<InvalidOperationException>(() =>
                {
                    foreach (var candidate in ConductorTickStepLedger.ObserveProjections(new[] { goal }))
                    {
                        Assert.Same(goal, candidate);
                        clock.Advance(TimeSpan.FromMilliseconds(11));
                        throw failure;
                    }
                });
                Assert.Same(failure, thrown);
            }
        });

        var detail = ledger.FormatAdmission(TimeSpan.Zero);
        Assert.Equal(1, Field(detail, "prep_projection_count"));
        Assert.Contains($" prep_projection_slowest={goal.Id.Value[..8]}:11 ", detail);
    }

    private static string RunFixture(FixtureTimeProvider clock, AgentOrchestratorKernel kernel,
        ConductorDriver driver, Action<string>? probe = null)
    {
        var loop = new ConductorBatchLoop(processCpuTime: () => TimeSpan.Zero, janitorialTimestamp: () => 0)
            .WithDiagnosticTimeProvider(clock);
        if (probe is not null) loop.WithStepProbe(probe);
        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
        return ConductorBatchLoopTestsStepLedger.Phase(tick, "acceptance-admission");
    }

    private static void AtTickClock(FixtureTimeProvider clock, Action action)
    {
        var calls = 0;
        var (kernel, _) = SimpleGoal();
        RunFixture(clock, kernel, MakeDriver(), name =>
        {
            if (name != "attempt-census") return;
            calls++;
            action();
        });
        Assert.Equal(1, calls);
    }

    private static Dictionary<string, (long Wall, long Cpu, long Count)> Steps(string line, string key)
    {
        var match = Regex.Match(line, $@"\b{key}=(\S+)");
        Assert.True(match.Success, line);
        return match.Groups[1].Value.Split(',').ToDictionary(entry => entry.Split(':')[0], entry =>
        {
            var fields = entry.Split(':');
            Assert.Equal(4, fields.Length);
            return (Number(fields[1]), Number(fields[2]), Number(fields[3]));
        }, StringComparer.Ordinal);
    }

    private static long Field(string line, string key)
    {
        var match = Regex.Match(line, $@"\b{key}=(-?\d+)(?: |$)");
        Assert.True(match.Success, line);
        return Number(match.Groups[1].Value);
    }

    private static long Number(string value) => long.Parse(value, CultureInfo.InvariantCulture);

    private static void AssertPhaseReconciles(string line)
    {
        Assert.Equal(Math.Max(0, Field(line, "elapsed_ms") - Steps(line, "admission_steps").Values.Sum(step => step.Wall)),
            Field(line, "unattributed_ms"));
        Assert.True(Field(line, "prep_unattributed_ms") >= 0, line);
    }

    private sealed class FixtureTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
