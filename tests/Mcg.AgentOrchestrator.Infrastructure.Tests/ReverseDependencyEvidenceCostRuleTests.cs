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

    [Fact]
    public void Evaluate_NoCompletedAttempts_SkipsWithUnavailableRate()
    {
        var result = ReverseDependencyEvidenceCostRule.Evaluate(10, 100, null);
        Assert.False(result.Run);
        Assert.Contains("rate could not be computed", result.Line);
        Assert.Contains("candidate_failure_rate=unavailable", result.Line);
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
    public void Evaluate_InvalidInput_Skips(double selection, double gate, double rate)
    {
        var result = ReverseDependencyEvidenceCostRule.Evaluate(selection, gate, rate);
        Assert.False(result.Run);
        Assert.Contains("invalid input", result.Line);
    }
}
