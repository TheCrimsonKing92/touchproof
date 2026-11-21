using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ReverseDependencyEvidenceCostRuleTests
{
    [Fact]
    public void Evaluate_NarrowSelection_Runs() =>
        Assert.True(ReverseDependencyEvidenceCostRule.Evaluate(10, 100, 0.25).Run);

    [Fact]
    public void Evaluate_ExactThreshold_Runs() =>
        Assert.True(ReverseDependencyEvidenceCostRule.Evaluate(25, 100, 0.25).Run);

    [Fact]
    public void Evaluate_WideSelection_SkipsWithAllInputs()
    {
        var result = ReverseDependencyEvidenceCostRule.Evaluate(30, 100, 0.25);
        Assert.False(result.Run);
        Assert.Contains("selection_seconds=30", result.Line);
        Assert.Contains("full_gate_seconds=100", result.Line);
        Assert.Contains("candidate_failure_rate=0.25", result.Line);
        Assert.Contains("decision=skip", result.Line);
    }

    [Theory]
    [InlineData(10, "10")]
    [InlineData(double.NaN, "NaN")]
    public void Evaluate_UnavailableRate_SkipsWithAllInputs(double selectionSeconds, string printedSelectionSeconds)
    {
        var result = ReverseDependencyEvidenceCostRule.Evaluate(selectionSeconds, 100, null);
        Assert.False(result.Run);
        Assert.Contains($"selection_seconds={printedSelectionSeconds}", result.Line);
        Assert.Contains("full_gate_seconds=100", result.Line);
        Assert.Contains("candidate_failure_rate=unavailable", result.Line);
        Assert.Contains("decision=skip", result.Line);
        Assert.Contains("reason=too few completed gates to compute a rate", result.Line);
    }

    [Fact]
    public void Evaluate_ZeroRate_SkipsPositiveCostAndRunsZeroCost()
    {
        Assert.False(ReverseDependencyEvidenceCostRule.Evaluate(1, 100, 0).Run);
        Assert.True(ReverseDependencyEvidenceCostRule.Evaluate(0, 100, 0).Run);
    }

    [Theory]
    [InlineData(double.NaN, 100, 0.5)]
    [InlineData(1, double.PositiveInfinity, 0.5)]
    [InlineData(-1, 100, 0.5)]
    [InlineData(1, 100, -0.1)]
    [InlineData(1, 100, 1.1)]
    public void Evaluate_InvalidInput_Skips(double selection, double gate, double? rate)
    {
        var result = ReverseDependencyEvidenceCostRule.Evaluate(selection, gate, rate);
        Assert.False(result.Run);
        Assert.Contains("invalid input", result.Line);
    }
}
