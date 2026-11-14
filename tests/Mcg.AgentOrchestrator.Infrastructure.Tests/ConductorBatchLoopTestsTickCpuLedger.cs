using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// The loop's shared infrastructure uses the same isolation as its sibling tests.
[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsTickCpuLedger(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void CompletedTickRecordOutlivesConductLogRotation()
    {
        var root = CreateTempDirectory("tick-cpu-ledger");
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var (kernel, _) = SimpleGoal("CPU accounting dispatch");
            var cpu = TimeSpan.Zero;
            var dispatchCalls = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: goal =>
                {
                    dispatchCalls++;
                    cpu += TimeSpan.FromMilliseconds(25);
                    return DispatchStartOutcome.Started([Assert.Single(goal.Tasks)]);
                });
            var loop = new ConductorBatchLoop(
                workspace: workspace,
                conductEventLogWriter: new ConductEventLogWriter(workspace.ConductEventsLogPath),
                loadTrxCoherenceVerdicts: _ => { },
                measuredSweep: _ => { cpu += TimeSpan.FromMilliseconds(40); return null; },
                processCpuTime: () => cpu);
            BatchTickSummary? tick = null;
            loop.Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                maxIterations: 1, onTick: value => tick = value);

            Assert.NotNull(tick);
            Assert.Equal(1, dispatchCalls);
            var summary = Assert.Single(tick.ProgressLines!, line => line.StartsWith("TICK_END ", StringComparison.Ordinal));
            Assert.Contains(" cpu_ms=65 cpu_sweep_ms=40 cpu_prewalk_ms=0 cpu_walk_ms=25", summary);
            Assert.True(File.Exists(workspace.ConductEventsLogPath));
            var rotatedPath = workspace.ConductEventsLogPath + ".rotated";
            File.Move(workspace.ConductEventsLogPath, rotatedPath);
            Assert.False(File.Exists(workspace.ConductEventsLogPath));
            Assert.True(File.Exists(rotatedPath));

            var ledgerPath = Path.Combine(root, ".orchestrator", "tick-cpu.jsonl");
            var record = JsonSerializer.Deserialize<TickCpuLedgerRecord>(
                Assert.Single(File.ReadAllLines(ledgerPath)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(record);
            Assert.Equal(tick.Tick, record.Tick);
            Assert.Equal(65, record.CpuMs);
            Assert.Equal(40, record.CpuSweepMs);
            Assert.Equal(0, record.CpuPrewalkMs);
            Assert.Equal(25, record.CpuWalkMs);
            Assert.True(record.HadGoalChanges);
            Assert.Equal(1, record.Version);
            Assert.Equal(TimeSpan.Zero, record.ObservedAt.Offset);
            var printedCpu = Regex.Match(summary, @" cpu_ms=(\d+)").Groups[1].Value;
            Assert.Equal(record.CpuMs.ToString(System.Globalization.CultureInfo.InvariantCulture), printedCpu);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void QuietTicksAppendEvenWhenNoSummaryIsEmitted()
    {
        var root = CreateTempDirectory("tick-cpu-quiet");
        try
        {
            var (kernel, _) = SimpleGoal();
            // A stable hold repeats across ticks; only its first disposition is emitted.
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningCount: () => 100);
            var cpu = TimeSpan.Zero;
            var ticks = new List<BatchTickSummary>();
            new ConductorBatchLoop(
                workspace: OrchestratorWorkspace.ForDirectory(root),
                loadTrxCoherenceVerdicts: _ => { },
                measuredSweep: _ => { cpu += TimeSpan.FromMilliseconds(17); return null; },
                processCpuTime: () => cpu).Run(
                    kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                    maxIterations: 3, watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false, onTick: ticks.Add);

            Assert.Equal(3, ticks.Count);
            var quiet = Assert.Single(ticks, tick => tick.Tick == 3);
            Assert.DoesNotContain(quiet.ProgressLines!, line =>
                line.StartsWith("TICK_END ", StringComparison.Ordinal) || line.StartsWith("TICK_SUMMARY ", StringComparison.Ordinal));
            var records = File.ReadAllLines(Path.Combine(root, ".orchestrator", "tick-cpu.jsonl"))
                .Select(line => JsonSerializer.Deserialize<TickCpuLedgerRecord>(line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
            Assert.Equal(new[] { 1, 2, 3 }, records.Select(record => record.Tick));
            Assert.All(records, record => Assert.Equal(17, record.CpuMs));
            Assert.False(records[2].HadGoalChanges);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AdmissionCpuMatchesPhaseTimingSummaryAndLedger()
    {
        var root = CreateTempDirectory("tick-cpu-admission");
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var (kernel, _) = SimpleGoal("Admission CPU ledger agreement");
            var cpu = TimeSpan.Zero;
            var dispatchCalls = 0;
            var probeCalls = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: goal =>
                {
                    dispatchCalls++;
                    cpu += TimeSpan.FromMilliseconds(25);
                    return DispatchStartOutcome.Started([Assert.Single(goal.Tasks)]);
                });
            var loop = new ConductorBatchLoop(
                workspace: workspace,
                loadTrxCoherenceVerdicts: _ => { },
                measuredSweep: _ => { cpu += TimeSpan.FromMilliseconds(40); return null; },
                processCpuTime: () => cpu, janitorialTimestamp: () => 0)
                .WithStepProbe(name =>
                {
                    if (name != "attempt-census") return;
                    probeCalls++;
                    cpu += TimeSpan.FromMilliseconds(30);
                });
            BatchTickSummary? tick = null;
            loop.Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                maxIterations: 1, onTick: value => tick = value);

            Assert.NotNull(tick);
            Assert.Equal(1, dispatchCalls);
            Assert.Equal(1, probeCalls);
            var phase = Assert.Single(tick.ProgressLines!, line => line.StartsWith(
                "PHASE_TIMING tick=1 phase=acceptance-admission ", StringComparison.Ordinal));
            var phaseCpu = long.Parse(Regex.Match(phase, @"\bcpu_ms=(\d+)").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(30, phaseCpu);
            var summary = Assert.Single(tick.ProgressLines!, line => line.StartsWith("TICK_END ", StringComparison.Ordinal));
            Assert.Contains($" cpu_walk_ms=25 cpu_admission_ms={phaseCpu}", summary);
            var ledgerPath = Path.Combine(root, ".orchestrator", "tick-cpu.jsonl");
            var record = JsonSerializer.Deserialize<TickCpuLedgerRecord>(
                Assert.Single(File.ReadAllLines(ledgerPath)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(record);
            Assert.Equal(tick.Tick, record.Tick);
            Assert.Equal(30, record.CpuAdmissionMs);
            Assert.Equal(phaseCpu, record.CpuAdmissionMs);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SweepAdmissionAndWalkCpuAreRecordedSeparately()
    {
        var root = CreateTempDirectory("tick-cpu-separate-phases");
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var (kernel, _) = SimpleGoal("Separate phase CPU accounting");
            var cpu = TimeSpan.Zero;
            var dispatchCalls = 0;
            var probeCalls = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: goal =>
                {
                    dispatchCalls++;
                    cpu += TimeSpan.FromMilliseconds(25);
                    return DispatchStartOutcome.Started([Assert.Single(goal.Tasks)]);
                });
            var loop = new ConductorBatchLoop(
                workspace: workspace,
                loadTrxCoherenceVerdicts: _ => { },
                measuredSweep: _ => { cpu += TimeSpan.FromMilliseconds(40); return null; },
                processCpuTime: () => cpu, janitorialTimestamp: () => 0)
                .WithStepProbe(name =>
                {
                    if (name != "attempt-census") return;
                    probeCalls++;
                    cpu += TimeSpan.FromMilliseconds(30);
                });
            BatchTickSummary? tick = null;
            loop.Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                maxIterations: 1, onTick: value => tick = value);

            Assert.NotNull(tick);
            Assert.Equal(1, dispatchCalls);
            Assert.Equal(1, probeCalls);
            var record = JsonSerializer.Deserialize<TickCpuLedgerRecord>(
                Assert.Single(File.ReadAllLines(Path.Combine(root, ".orchestrator", "tick-cpu.jsonl"))),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(record);
            Assert.Equal(tick.Tick, record.Tick);
            Assert.Equal(40, record.CpuSweepMs);
            Assert.Equal(30, record.CpuAdmissionMs);
            Assert.Equal(25, record.CpuWalkMs);
            Assert.Equal(0, record.CpuPrewalkMs);
            Assert.Equal(95, record.CpuMs);
            var summary = Assert.Single(tick.ProgressLines!, line => line.StartsWith("TICK_END ", StringComparison.Ordinal));
            var printedCpu = long.Parse(Regex.Match(summary, @"\bcpu_ms=(\d+)").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(record.CpuMs, printedCpu);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacyLineWithoutAdmissionPropertyParsesWithZeroAdmission()
    {
        const string legacyLine = """
            {"observedAt":"2026-10-02T00:00:00Z","tick":1,"cpuMs":100,"cpuSweepMs":30,"cpuPrewalkMs":20,"cpuWalkMs":50,"hadGoalChanges":false,"version":1}
            """;
        var record = JsonSerializer.Deserialize<TickCpuLedgerRecord>(
            legacyLine, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(record);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), record.ObservedAt);
        Assert.Equal(1, record.Tick);
        Assert.Equal(100, record.CpuMs);
        Assert.Equal(30, record.CpuSweepMs);
        Assert.Equal(20, record.CpuPrewalkMs);
        Assert.Equal(50, record.CpuWalkMs);
        Assert.False(record.HadGoalChanges);
        Assert.Equal(1, record.Version);
        Assert.Null(record.ExperimentArms);
        Assert.Equal(0, record.CpuAdmissionMs);
    }
}
