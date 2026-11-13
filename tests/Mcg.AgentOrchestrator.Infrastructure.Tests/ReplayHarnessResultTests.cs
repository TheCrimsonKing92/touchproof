using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: local in-memory cases and outcome maps only.
public sealed class ReplayHarnessResultTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_MixedCases_ReportsExactDiffRateAndOutcomes()
    {
        ReplayHarnessCase[] cases =
        [
            ReplayHarnessCase.CreateReplayable("same", At, "a", "a"),
            ReplayHarnessCase.CreateReplayable("changed", At, "a", "b"),
            ReplayHarnessCase.CreateReplayable("ordinal", At, "a", "A"),
            ReplayHarnessCase.CreateNonReplayable("excluded", At, "missing input", "a", "b"),
            ReplayHarnessCase.CreateReplayable("training", At, "a")
        ];
        var result = new ReplayHarnessResult(cases, new Dictionary<string, double> { ["gate-minutes"] = 12.5 });

        Assert.Equal(cases, result.Cases);
        Assert.Equal(new[] { cases[1], cases[2] }, result.DecisionDiff);
        Assert.Equal(4, result.ReplayableCount);
        Assert.Equal(4.0 / 5, result.ReplayabilityRate);
        Assert.Equal("0.800", result.ReplayabilityRateText);
        Assert.Equal(12.5, result.ModeledOutcomes["gate-minutes"]);
    }

    [Fact]
    public void Create_EmptyCases_ReportsNotApplicable()
    {
        var result = new ReplayHarnessResult([], new Dictionary<string, double>());
        Assert.Empty(result.Cases);
        Assert.Empty(result.DecisionDiff);
        Assert.Equal(0, result.ReplayableCount);
        Assert.Null(result.ReplayabilityRate);
        Assert.Equal("n/a", result.ReplayabilityRateText);
    }

    [Fact]
    public void Create_ExcludedCases_ReportsZeroRateAndSnapshotsInputs()
    {
        var excluded = ReplayHarnessCase.CreateNonReplayable("excluded", At, "unavailable");
        var cases = new List<ReplayHarnessCase> { excluded };
        var outcomes = new Dictionary<string, double> { ["hits"] = 0 };
        var result = new ReplayHarnessResult(cases, outcomes);
        cases.Clear();
        outcomes["hits"] = 100;

        Assert.Equal(new[] { excluded }, result.Cases);
        Assert.False(excluded.Replayable);
        Assert.Equal("unavailable", excluded.NotReplayableReason);
        Assert.Empty(result.DecisionDiff);
        Assert.Equal(0.0, result.ReplayabilityRate);
        Assert.Equal("0.000", result.ReplayabilityRateText);
        Assert.Equal(0.0, result.ModeledOutcomes["hits"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateNonReplayable_BlankReason_Throws(string reason)
    {
        Assert.Throws<ArgumentException>(() => ReplayHarnessCase.CreateNonReplayable("case", At, reason));
    }
}
