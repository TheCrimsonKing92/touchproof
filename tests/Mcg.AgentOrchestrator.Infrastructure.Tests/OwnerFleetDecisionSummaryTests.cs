using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Text;

// Unique filesystem roots and pure timing fixtures; safe to run in parallel.
public sealed class OwnerFleetDecisionSummaryTests
{
    [Theory]
    [InlineData(10d, 15d, "1.5x")]
    [InlineData(null, 15d, "-")]
    [InlineData(10d, null, "-")]
    [InlineData(0d, 15d, "-")]
    [InlineData(-1d, 15d, "-")]
    [InlineData(double.NaN, 15d, "-")]
    [InlineData(10d, double.PositiveInfinity, "-")]
    [InlineData(10d, 0d, "0.0x")]
    public void LaneRatioHandlesMissingAndInvalidMedians(double? solo, double? concurrent, string expected)
    {
        var rows = OwnerFleetLaneTable.Rows([new("timing", 3, 10, 10, solo, concurrent)], 36, false);
        Assert.Equal(expected, rows[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.All(rows, row => Assert.Equal(36, row.GetColumns()));
    }

    [Fact]
    public void OverallSlowdownUsesSampleWeightsAndHiddenLanes()
    {
        RemoteExecutorLaneSeconds[] lanes = [new("one", 1, 100, 100, 10, 30),
            new("two", 2, 10, 10, 10, 15), new("missing", 100, 100, 100, null, 10),
            new("zero", 0, 10, 10, 1, 100), new("empty-a", 1, 99, 99, null, null),
            new("empty-b", 1, 98, 98, null, null), new("hidden", 3, 1, 1, 10, 40)];
        var model = new OwnerFleetViewModel(new(true, 1, 0, 0),
            [new("alpha", OwnerFleetReachability.Unknown, [], null, 2, 0, null, null, null, lanes)]);
        Assert.Equal("slowdown 3.0x decision none", OwnerFleetLaneTable.Summary(lanes, null, 56));
        Assert.Contains("slowdown 3.0x decision none", OwnerFleetDiagram.Render(model));
        Assert.Contains("slowdown 3.0x decision none", OwnerFleetDiagram.Render(model, expanded: true));
        Assert.DoesNotContain("│ hidden", OwnerFleetDiagram.Render(model));
        Assert.Contains("│ hidden", OwnerFleetDiagram.Render(model, expanded: true));
    }

    [Fact]
    public void NoEligibleSampleWeightShowsDash()
    {
        RemoteExecutorLaneSeconds[] lanes = [new("missing", 3, 10, 10, null, 20),
            new("zero", 0, 10, 10, 10, 20), new("negative", -1, 10, 10, 10, 20)];
        Assert.Equal("slowdown - decision none", OwnerFleetLaneTable.Summary(lanes, null, 36));
        Assert.Equal("slowdown - decision none", OwnerFleetLaneTable.Summary([], null, 36));
        Assert.Single(OwnerFleetLaneTable.Rows([], 36, false));
    }

    [Fact]
    public void SortingUsesUnroundedCostThenOrdinalNameWithNonFiniteLast()
    {
        RemoteExecutorLaneSeconds[] lanes = [new("unknown", 1, double.NaN, 1, null, null),
            new("z", 1, 1.02, 1, null, null), new("b", 1, 1.01, 1, null, null),
            new("a", 1, 1.01, 1, null, null), new("infinite", 1, double.PositiveInfinity, 1, null, null)];
        Assert.Equal(new[] { "z", "a", "b", "infinite", "unknown" },
            OwnerFleetLaneTable.Rows(lanes, 56, true).Skip(1).Select(row => row.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]));
    }

    [Theory]
    [InlineData(OwnerFleetDiagram.MinimumWidth)]
    [InlineData(OwnerFleetDiagram.DefaultWidth)]
    public void SeededLedgerShowsLatestDecisionAndWeightedSummary(int width)
    {
        using var fixture = new Fixture();
        ExecutorSlotDecisionLedger.Append(fixture.Ledger, Decision("hold", 2, 2));
        var latest = Decision("grow", 2, 3) with { EvaluationId = "new", ObservedAt = Fixture.At.AddMinutes(1) };
        ExecutorSlotDecisionLedger.Append(fixture.Ledger, latest);
        var model = fixture.Load();
        var alpha = model.Executors.Single(node => node.Id == "alpha");
        Assert.Equal("new", alpha.LatestDecision?.EvaluationId);
        var text = OwnerFleetDiagram.Render(model, width);
        Assert.Contains("slowdown 2.0x decision grow 2->3", text);
        Assert.DoesNotContain("decision hold", text);
        Assert.All(text.Split('\n'), row => Assert.Equal(width, row.GetColumns()));
        Assert.Null(model.Executors.Single(node => node.Id == "beta").LatestDecision);
        Assert.Contains("slowdown - decision none", text);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("malformed")]
    [InlineData("invalid-state")]
    [InlineData("locked")]
    [InlineData("no-match")]
    public void UnavailableDecisionLedgerKeepsFleetUsable(string kind)
    {
        using var fixture = new Fixture();
        if (kind == "malformed") File.WriteAllText(fixture.Ledger, "{not-json}");
        if (kind == "invalid-state") ExecutorSlotDecisionLedger.Append(fixture.Ledger,
            Decision("hold", 2, 2) with { ControllerState = null! });
        if (kind == "no-match") ExecutorSlotDecisionLedger.Append(fixture.Ledger,
            Decision("grow", 2, 3) with { ExecutorId = "other" });
        using var locked = kind == "locked" ? new FileStream(fixture.Ledger, FileMode.Create, FileAccess.ReadWrite, FileShare.None) : null;
        var model = fixture.Load();
        Assert.All(model.Executors, node => Assert.Null(node.LatestDecision));
        var text = OwnerFleetDiagram.Render(model);
        Assert.Contains("Executor alpha", text);
        Assert.Contains("slowdown 2.0x decision none", text);
        Assert.Equal(2, model.Executors.Count);
    }

    [Fact]
    public void LongDecisionActionIsCleanedAndPreservesSlotTransition()
    {
        var row = Decision("hold\u001b\t" + new string('界', 40), 12, 13);
        var summary = OwnerFleetLaneTable.Summary([], row, 36);
        Assert.EndsWith(" 12->13", summary);
        Assert.Contains('…', summary);
        Assert.DoesNotContain('\u001b', summary);
        Assert.DoesNotContain('\t', summary);
        Assert.InRange(summary.GetColumns(), 1, 36);
    }

    private static ExecutorSlotDecisionRecord Decision(string action, int current, int proposed)
    {
        var settings = new ExecutorSlotControllerSettings();
        var state = new ExecutorSlotControllerState(current, settings);
        var decision = new ExecutorSlotDecisionResult(action, action, current, proposed, false, "not displayed", 3, 0,
            new Dictionary<string, int>(), [], null, null, null, 0, state);
        return new("old", Fixture.At, "alpha", settings, decision, state, "recorded");
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset At = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-summary-" + Guid.NewGuid().ToString("N"));
        private string DirectoryPath => Path.Combine(_root, ".orchestrator");
        internal string Ledger => Path.Combine(DirectoryPath, ExecutorSlotDecisionLedger.FileName);

        internal Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(Path.Combine(DirectoryPath, "remote-lane-executors.json"),
                """{"executors":[{"id":"alpha","slots":2},{"id":"beta","slots":1}],"lanes":[]}""");
            File.WriteAllLines(Path.Combine(DirectoryPath, RemoteExecutorHealthLedger.FileName),
            [
                """{"observed_at":"2026-11-08T08:00:00Z","executor_id":"alpha","gate_attempt_id":"a","lane":"timing","outcome":"accepted","attempt":{"last_status":{"seconds":10},"steps":[{"started_at":"2026-11-08T07:59:00Z"}]}}""",
                """{"observed_at":"2026-11-08T09:00:00Z","executor_id":"alpha","gate_attempt_id":"b","lane":"timing","outcome":"accepted","attempt":{"last_status":{"seconds":20},"steps":[{"started_at":"2026-11-08T08:59:00Z"}]}}""",
                """{"observed_at":"2026-11-08T09:00:10Z","executor_id":"alpha","gate_attempt_id":"c","lane":"timing","outcome":"accepted","attempt":{"last_status":{"seconds":20},"steps":[{"started_at":"2026-11-08T08:59:10Z"}]}}"""
            ]);
        }

        internal OwnerFleetViewModel Load() => OwnerFleetViewModelBuilder.Load(OrchestratorWorkspace.ForDirectory(_root),
            new OwnerConsoleHarness().Liveness, new Clock());
        private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At; }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
