using Mcg.AgentOrchestrator.App.OwnerConsole;
using static OwnerConsoleGateStatusTestFixture;

// Parallel-safe: unique temporary files and an injected clock; no global state or processes.
public sealed class OwnerConsoleGateBaselineCountTests
{
    [Fact]
    public void EightCompletedLanesAndNineBaselinesShowSeventeenOfThirtyFive()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture(Enumerable.Range(0, 26)
            .Select(index => new OwnerConsoleGateManifestLane("Lane " + index, 100)).ToArray());
        fixture.WritePlan();
        var events = Enumerable.Range(0, 8).Select(index => Progress(-30, "shard-complete", "Lane " + index,
            1000, " outcome=passed")).Concat(Enumerable.Range(0, 9).Select(index =>
            Progress(-20, "check-complete", Baseline(index), 1000, " outcome=passed"))).ToArray();
        // Repeated observations for the same execution/check must not inflate completion.
        var gate = fixture.Load(events.Concat(events));

        Assert.Equal(17, gate.DoneCount);
        Assert.Equal(35, gate.TotalCount);
        var count = OwnerConsoleGateCount.Of(gate);
        Assert.Equal(17, count.Done);
        Assert.Equal(35, count.Total);
        Assert.Equal("17/35", OwnerConsoleGateFormatter.CountText(gate));
        Assert.StartsWith("in gate: 17/35,", Board(gate));
        Assert.Equal(8, count.Passed);
        Assert.Contains(" x 0 failed   > 0 running   + 8 passed   = 0 cached   . 0 queued",
            OwnerConsoleGateLayout.Lines(gate, 180, false));
    }

    [Fact]
    public void RunningBaselineEntersDenominatorAndRemainingBeforeCompletion()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan();
        var gate = fixture.Load([Progress(0, "verification-check", Baseline(0), 100000)],
            [new(Baseline(0), 400, Now)]);

        Assert.Equal(0, gate.DoneCount);
        Assert.Equal(2, gate.TotalCount);
        Assert.Equal(300, gate.RemainingSeconds); // Baseline remainder exceeds the 100s lane.
        Assert.True(gate.HasEta);
        Assert.Equal("in gate: 0/2, ~5m left", Board(gate));
        Assert.Equal(0, OwnerConsoleGateCount.Of(gate).Running); // Comparison tiles stay separate.
    }

    [Fact]
    public void ExpectedBaselineChecksAreCountedBeforeAnyCompletionAndEstimatedSerially()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan(scheduled: ["A", Baseline(0), Baseline(1)]);
        var gate = fixture.Load(history: [new(Baseline(0), 200, Now), new(Baseline(1), 300, Now)]);

        Assert.Equal(0, gate.DoneCount);
        Assert.Equal(3, gate.TotalCount);
        Assert.Equal("0/3", OwnerConsoleGateFormatter.CountText(gate));
        Assert.Equal(500, gate.RemainingSeconds); // Baselines share one serial queue.
    }

    [Fact]
    public void BaselineRunningAlongsideLanesDoesNotIncreaseLaneConcurrency()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture([new("A", 100), new("B", 100)]);
        fixture.WritePlan(scheduled: ["A", "B", Baseline(0)]);
        var gate = fixture.Load([
            Progress(0, "verification-check", "A"),
            Progress(0, "verification-check", Baseline(0), 1000)
        ], [new(Baseline(0), 10, Now)]);

        Assert.Equal(3, gate.TotalCount);
        Assert.Equal(200, gate.RemainingSeconds); // One lane slot: A then B, alongside baseline work.
        Assert.Equal(1, OwnerConsoleGateCount.Of(gate).Running);
    }

    [Fact]
    public void RetriedBaselineBecomesUnfinishedWithoutDuplicatingTheCheck()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan();
        var gate = fixture.Load([
            Progress(-50, "check-complete", Baseline(0), 1000, " outcome=passed"),
            Progress(0, "verification-check", Baseline(0), 0)
        ], [new(Baseline(0), 400, Now)]);

        Assert.Equal(0, gate.DoneCount);
        Assert.Equal(2, gate.TotalCount);
        Assert.Equal(400, gate.RemainingSeconds);
        Assert.Equal(OwnerConsoleGateLaneState.Running, Assert.Single(gate.Lanes, row => row.Name == Baseline(0)).State);
    }

    [Fact]
    public void MissingBaselineHistoryKeepsEtaUnknown()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan();
        var gate = fixture.Load([Progress(0, "verification-check", Baseline(0))]);

        Assert.Equal(2, gate.TotalCount);
        Assert.False(gate.HasEta);
        Assert.Null(gate.OverrunSeconds);
        Assert.Equal("in gate: 0/2, ETA unknown", Board(gate));
    }

    [Fact]
    public void MissingPlanKeepsDecisionUnknownButRetainsObservedBaselineCount()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        var gate = fixture.Load([Progress(0, "check-complete", Baseline(0), 1000, " outcome=passed")]);

        Assert.False(gate.LanePlanAvailable);
        Assert.Equal(1, gate.DoneCount);
        Assert.Equal(1, gate.TotalCount);
        Assert.Equal(1, OwnerConsoleGateCount.Of(gate).Done);
        Assert.Equal(1, OwnerConsoleGateCount.Of(gate).Total);
        Assert.Equal("in gate: schedule unknown, ETA unknown", Board(gate));
    }

    [Fact]
    public void OlderSnapshotsWithoutPlanFlagRetainTheirPopulation()
    {
        var gate = new OwnerConsoleGateProgress(true, "solo", [new(GoalId, "Fixture")], Now,
            TimeSpan.Zero, [new(Baseline(0), OwnerConsoleGateLaneState.Done, "passed", "", TimeSpan.Zero, 30, false)],
            0, 100, 0, 0);

        Assert.Equal(0, OwnerConsoleGateCount.Of(gate).Total);
        Assert.Equal("in gate: 0/0, ~2m left", Board(gate));
    }

    private static string Baseline(int index) => $"project {index} [trusted main baseline]";
}
