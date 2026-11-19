using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixed instants, in-memory gates, an owned log, and an AsyncLocal writer.
public sealed class CostGoalOutcomeBaselineTests
{
    private static readonly DateTimeOffset FiledAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Baseline_GatesRecordedAtOrAfterFiling_NeverEnter()
    {
        var line = new OutcomeMetricLine("selection-milliseconds", "decrease", "10%", 3);
        var gates = new[]
        {
            Gate("a", FiledAt.AddDays(-4), 500),
            Gate("b", FiledAt.AddDays(-3), 30),
            Gate("c", FiledAt.AddDays(-2), 50),
            Gate("d", FiledAt.AddDays(-1), 10),
            Gate("equal", FiledAt, 800),
            Gate("later", FiledAt.AddTicks(1), 900)
        };

        var baseline = Assert.Single(CostGoalOutcomeMetric.BaselinesAt([line], gates.Reverse(), FiledAt));

        Assert.Equal(new OutcomeMetricBaseline(line, 30, 3, null), baseline);
    }

    [Fact]
    public void Baseline_SmallerSamplesAndTies_UseStableOrderingAndAvailability()
    {
        var time = FiledAt.AddDays(-1);
        var gates = new[]
        {
            Gate("d", time, 500, null),
            Gate("c", time, 30, null, "Full"),
            Gate("b", time, 20, 7),
            Gate("a", time, 10, 3, "Full"),
            Gate("newest", time.AddHours(1), 900, 0, "No changed source")
        };
        var selection = new OutcomeMetricLine("selection-milliseconds", "decrease", "10", 3);
        var classes = new OutcomeMetricLine("executed-class-count", "decrease", "5", 5);
        var rate = new OutcomeMetricLine("full-gate-rate", "decrease", ".4", 10);
        var worker = new OutcomeMetricLine("process-launches", "decrease", "10", 3);

        var baselines = CostGoalOutcomeMetric.BaselinesAt([selection, classes, rate, worker], gates, FiledAt);

        Assert.Collection(baselines,
            row => Assert.Equal(new(selection, 20, 3, null), row),
            row => Assert.Equal(new(classes, null, 2, "baseline-unavailable"), row),
            row => Assert.Equal(new(rate, 0.6, 5, null), row),
            row => Assert.Equal(new(worker, null, 0, "no-gate-source"), row));
    }

    [Fact]
    public void Baseline_EvenPartialSample_UsesMeanOfTheMiddlePair()
    {
        var line = new OutcomeMetricLine("executed-class-count", "decrease", "5", 8);
        var gates = new[]
        {
            Gate("a", FiledAt.AddDays(-4), 1, 90),
            Gate("b", FiledAt.AddDays(-3), 1, 4),
            Gate("c", FiledAt.AddDays(-2), 1, 11),
            Gate("d", FiledAt.AddDays(-1), 1, 2)
        };

        var baseline = Assert.Single(CostGoalOutcomeMetric.BaselinesAt([line], gates, FiledAt));

        Assert.Equal(new OutcomeMetricBaseline(line, 7.5, 4, null), baseline);
    }

    [Fact]
    public async Task PhaseTiming_RoundTripsAcrossLogRotation()
    {
        var root = Directory.CreateTempSubdirectory("tick-step-ledger-").FullName;
        var path = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
        var previous = ConductorBatchLoop.CurrentConductEventLogWriter.Value;
        var now = FiledAt.AddMinutes(-2);
        var firstAt = now;
        var secondAt = now.AddMinutes(1);
        const string first = "PHASE_TIMING tick=1 phase=sweep elapsed_ms=40 " +
            "sweep_steps=owned-root-reap:30:7,maintenance:NaN:1,host-health:-1:1 " +
            "kernel_reload_steps=kernel-merge:0:5,remainder:7:1";
        const string second = "PHASE_TIMING tick=1 phase=sweep elapsed_ms=15 sweep_steps=owned-root-reap:10:2";
        try
        {
            ConductorBatchLoop.CurrentConductEventLogWriter.Value = new(path, maxBytes: 1, utcNow: () => now);
            await AsyncLocalConsoleRouter.Out.CaptureLocalAsync(() =>
            {
                ConductorBatchLoop.EmitProgress(first);
                now = secondAt;
                ConductorBatchLoop.EmitProgress(second);
                return Task.CompletedTask;
            });

            var rotated = Assert.Single(Directory.GetFiles(root, "conduct-events-*.log"));
            var firstLine = Assert.Single(File.ReadAllLines(rotated));
            var secondLine = Assert.Single(File.ReadAllLines(path));
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            Assert.Equal(new ConductEventRecord(firstAt, "phase-timing", null, first),
                JsonSerializer.Deserialize<ConductEventRecord>(firstLine, options));
            Assert.Equal(new ConductEventRecord(secondAt, "phase-timing", null, second),
                JsonSerializer.Deserialize<ConductEventRecord>(secondLine, options));

            TickStepObservation[] expected =
            [
                new(firstAt, 1, "owned-root-reap", 30),
                new(firstAt, 1, "kernel-merge", 0),
                new(secondAt, 1, "owned-root-reap", 10)
            ];
            Assert.Equal(expected, TickStepLedgerReader.Read(path).ToArray());

            File.AppendAllText(path, firstLine + Environment.NewLine);
            Assert.Equal(expected, TickStepLedgerReader.Read(path).ToArray());
        }
        finally
        {
            ConductorBatchLoop.CurrentConductEventLogWriter.Value = previous;
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    internal static OutcomeGateObservation Gate(string id, DateTimeOffset startedAt, double milliseconds,
        int? classCount = 1, string? reason = null, string? commit = "landed") =>
        new(id, startedAt, startedAt, commit, Selection(milliseconds, classCount, reason));

    internal static AcceptanceSelectedExecutionRecord Selection(double milliseconds, int? classCount, string? reason) =>
        new("Enforce", reason is null ? "plan" : "full-gate", null, reason, [], [], [], [],
            classCount, null, null, null, milliseconds, false);
}
