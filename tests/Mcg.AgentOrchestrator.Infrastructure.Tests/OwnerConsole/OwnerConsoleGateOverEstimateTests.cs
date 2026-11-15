using Mcg.AgentOrchestrator.App.OwnerConsole;
using static OwnerConsoleGateStatusTestFixture;

// Parallel-safe: private artifacts and explicit timestamps; no wall-clock decisions or processes.
public sealed class OwnerConsoleGateOverEstimateTests
{
    [Fact]
    public void UnfinishedGatePastEstimateShowsElapsedOverrunAcrossViews()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan(Decision("full-gate", "ExampleReason"));
        var gate = fixture.Load([Progress(0, "verification-check", "A", 220000)]);

        Assert.Equal(120, gate.OverrunSeconds);
        Assert.Equal(0, gate.DoneCount);
        Assert.Equal("in gate: full gate 0/1, over estimate by 2m (ExampleReason)", Board(gate));
        Assert.Contains("over estimate by 2m", OwnerConsoleGateFormatter.ProgressLine(gate));
        Assert.Contains("over estimate by 2m", OwnerConsoleGateFormatter.RunLine(gate));
        Assert.Contains("over estimate by 2m", OwnerGoalDetailGateLine.Text(gate));
        Assert.Contains("over 2m", OwnerConsoleGateFormatter.StatusSegment(gate));
        Assert.DoesNotContain("~0m", Board(gate));
        Assert.DoesNotContain("~0m", Detail(gate));
    }

    [Theory]
    [InlineData(99000, "~<1m left", null)]
    [InlineData(99500, "over estimate by <1m", 0)]
    [InlineData(100000, "over estimate by <1m", 0)]
    [InlineData(130000, "over estimate by <1m", 30)]
    [InlineData(160000, "over estimate by 1m", 60)]
    public void DisplayBoundaryNeverShowsZeroRemainingWhileChecksRemain(int elapsedMs, string expected, int? overrun)
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan();
        var gate = fixture.Load([Progress(0, "verification-check", "A", elapsedMs)]);

        Assert.Equal(overrun, gate.OverrunSeconds);
        Assert.Equal("in gate: 0/1, " + expected, Board(gate));
        Assert.DoesNotContain("~0m", OwnerConsoleGateFormatter.ProgressLine(gate));
    }

    [Fact]
    public void BaselineOverrunRemainsVisibleAfterAllLanesFinish()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan();
        const string baseline = "project [trusted main baseline]";
        var gate = fixture.Load([
            Progress(-10, "shard-complete", "A", 1000, " outcome=passed"),
            Progress(0, "verification-check", baseline, 220000)
        ], [new(baseline, 100, Now)]);

        Assert.Equal(1, gate.DoneCount);
        Assert.Equal(2, gate.TotalCount);
        Assert.Equal(120, gate.OverrunSeconds);
        Assert.Equal("in gate: 1/2, over estimate by 2m", Board(gate));
        Assert.True(gate.Fraction < 1);
    }

    [Fact]
    public void CompletedChecksClearOverrunAndKeepExistingCompletionText()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture();
        fixture.WritePlan();
        var gate = fixture.Load([
            Progress(-10, "verification-check", "A", 220000),
            Progress(0, "shard-complete", "A", 230000, " outcome=passed")
        ]);

        Assert.Equal(1, gate.DoneCount);
        Assert.Equal(1, gate.TotalCount);
        Assert.Null(gate.OverrunSeconds);
        Assert.Equal("in gate: 1/1, ~0m left", Board(gate));
        Assert.Equal(1, gate.Fraction);
    }

    [Fact]
    public void UnknownEstimateDoesNotManufactureAnOverrun()
    {
        using var fixture = new OwnerConsoleGateStatusTestFixture([new("A", 0)]);
        fixture.WritePlan();
        var gate = fixture.Load([Progress(0, "verification-check", "A", 220000)]);

        Assert.False(gate.HasEta);
        Assert.Null(gate.OverrunSeconds);
        Assert.Equal("in gate: 0/1, ETA unknown", Board(gate));
        Assert.Equal("ETA unknown", OwnerConsoleGateFormatter.ProgressLine(gate));
    }
}
