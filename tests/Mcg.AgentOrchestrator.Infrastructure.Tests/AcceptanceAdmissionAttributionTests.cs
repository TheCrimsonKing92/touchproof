using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;

// These fixtures use in-memory goals and injected clocks; they neither acquire
// build slots nor mutate the process environment, so need no owned collection.
public sealed class AcceptanceAdmissionAttributionTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void HoldCohortMembers_FiveSecondWait_ReportsWallWithoutCpu()
    {
        var clock = new FixtureTimeProvider();
        var line = RunFixture(clock, () => TimeSpan.Zero, name =>
        {
            if (name == "hold-cohort-members") clock.Advance(TimeSpan.FromSeconds(5));
        });

        Assert.Equal((5000L, 0L, 1L), Steps(line)["hold-cohort-members"]);
        Assert.InRange(Field(line, "unattributed_ms"), 0, 99);
        Assert.Equal(5000, Field(line, "elapsed_ms"));
        Assert.Equal((0L, 0L, 2L), Steps(line)["begin-tick"]);
        AssertReconciles(line);
    }

    [Fact]
    public void AdmissionLine_AllStableNames_IncludeWallCpuAndCount()
    {
        var line = RunFixture(new FixtureTimeProvider(), () => TimeSpan.Zero);
        var expected = new[]
        {
            "attempt-census", "attempt-reconcile", "grouped-admission-prep", "merge-train", "pair-cohort", "solo-admission",
            "activation-probe-record", "priority-order", "hold-cohort-members", "hold-lone-ready",
            "priority-guard", "deferral-summary", "admission-pre-batch", "begin-tick"
        };

        Assert.Equal(expected, ConductorTickStepLedger.AdmissionNames);
        Assert.Equal(expected, Steps(line).Keys);
        Assert.All(expected, name => Assert.Matches($@"(?:=|,){name}:\d+:\d+:\d+(?:,| )", line));
        AssertReconciles(line);
    }

    [Theory]
    [InlineData("attempt-census", 1)]
    [InlineData("begin-tick", 2)]
    [InlineData("admission-pre-batch", 3)]
    public void RepeatedStep_ReportsAccumulatedWallCpuAndCount(string step, int calls)
    {
        var clock = new FixtureTimeProvider();
        var cpu = TimeSpan.Zero;
        var line = RunFixture(clock, () => cpu, name =>
        {
            if (name != step) return;
            clock.Advance(TimeSpan.FromMilliseconds(17));
            cpu += TimeSpan.FromMilliseconds(3);
        });

        Assert.Equal((17L * calls, 3L * calls, (long)calls), Steps(line)[step]);
        Assert.Equal(17L * calls, Field(line, "elapsed_ms"));
        Assert.Equal(3L * calls, Field(line, "cpu_ms"));
        AssertReconciles(line);
    }

    [Fact]
    public void NestedStep_ChargesWallAndCpuExclusively()
    {
        var clock = new FixtureTimeProvider();
        var cpu = TimeSpan.Zero;
        var line = RunFixture(clock, () => cpu, name =>
        {
            if (name != "attempt-census") return;
            ConductorTickStepLedger.Measure("begin-tick", () =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(12));
                cpu += TimeSpan.FromMilliseconds(4);
            });
            clock.Advance(TimeSpan.FromMilliseconds(7));
            cpu += TimeSpan.FromMilliseconds(2);
        });

        Assert.Equal((7L, 2L, 1L), Steps(line)["attempt-census"]);
        Assert.Equal((12L, 4L, 3L), Steps(line)["begin-tick"]);
        Assert.Equal(19, Field(line, "elapsed_ms"));
        Assert.Equal(0, Field(line, "unattributed_ms"));
        AssertReconciles(line);
    }

    [Fact]
    public void FractionalSteps_RemainderUsesReportedWallMilliseconds()
    {
        var clock = new FixtureTimeProvider();
        var line = RunFixture(clock, () => TimeSpan.Zero, name =>
        {
            if (name is "attempt-census" or "admission-pre-batch")
                clock.Advance(TimeSpan.FromTicks(6000));
        });

        Assert.Equal((1L, 0L, 3L), Steps(line)["admission-pre-batch"]);
        Assert.Equal((0L, 0L, 1L), Steps(line)["attempt-census"]);
        Assert.Equal(2, Field(line, "elapsed_ms"));
        Assert.Equal(1, Field(line, "unattributed_ms"));
        AssertReconciles(line);
    }

    [Fact]
    public void NamedWallExceedsPhase_RemainderIsFlooredAndUncalledStepsAreZero()
    {
        var clock = new FixtureTimeProvider();
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
        RunFixture(clock, () => TimeSpan.Zero, name =>
        {
            if (name != "attempt-census") return;
            // Reuse the tick's scoped fake clock for an independent ledger.
            using (ledger.Activate())
                ConductorTickStepLedger.Measure("hold-cohort-members", () => clock.Advance(TimeSpan.FromMilliseconds(10)));
        });

        var detail = ledger.FormatAdmission(TimeSpan.Zero);
        Assert.Equal((10L, 0L, 1L), Steps(detail)["hold-cohort-members"]);
        Assert.Equal(0, Field(detail, "unattributed_ms"));
        Assert.Equal((0L, 0L, 0L), Steps(detail)["solo-admission"]);
        Assert.Equal(ConductorTickStepLedger.AdmissionNames.Count, Steps(detail).Count);
    }

    private static string RunFixture(FixtureTimeProvider clock, Func<TimeSpan> cpu, Action<string>? probe = null)
    {
        var (kernel, _) = SimpleGoal("Admission attribution");
        var loop = new ConductorBatchLoop(processCpuTime: cpu, janitorialTimestamp: () => 0)
            .WithDiagnosticTimeProvider(clock);
        if (probe is not null) loop.WithStepProbe(probe);
        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, MakeDriver());
        return ConductorBatchLoopTestsStepLedger.Phase(tick, "acceptance-admission");
    }

    private static Dictionary<string, (long Wall, long Cpu, long Count)> Steps(string line)
    {
        var match = Regex.Match(line, @"\badmission_steps=(\S+)");
        Assert.True(match.Success, line);
        return match.Groups[1].Value.Split(',').ToDictionary(entry => entry.Split(':')[0], entry =>
        {
            var fields = entry.Split(':');
            Assert.Equal(4, fields.Length);
            return (Number(fields[1]), Number(fields[2]), Number(fields[3]));
        }, StringComparer.Ordinal);
    }

    private static long Field(string line, string name)
    {
        var match = Regex.Match(line, $@"\b{name}=(-?\d+)(?: |$)");
        Assert.True(match.Success, line);
        return Number(match.Groups[1].Value);
    }

    private static long Number(string value) => long.Parse(value, CultureInfo.InvariantCulture);

    private static void AssertReconciles(string line)
    {
        var elapsed = Field(line, "elapsed_ms");
        var remainder = Field(line, "unattributed_ms");
        Assert.Equal(Math.Max(0, elapsed - Steps(line).Values.Sum(step => step.Wall)), remainder);
        Assert.InRange(remainder, 0, elapsed);
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
