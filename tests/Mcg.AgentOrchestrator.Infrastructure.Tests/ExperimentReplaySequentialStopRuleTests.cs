using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: each test owns its deterministic in-memory rule instances.
public sealed class ExperimentReplaySequentialStopRuleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Observe_ClearNonzeroMean_StopsForDifference(int sign)
    {
        var rule = new ExperimentReplaySequentialStopRule(0.05, 1, 5);
        Assert.Null(rule.Current);
        var first = rule.Observe(sign * 99);
        Assert.Equal("continue", first.State);
        Assert.Null(first.Lower);
        Assert.Null(first.Upper);
        for (var i = 1; i < 40; i++) rule.Observe(sign * (i % 2 == 0 ? 99 : 101));

        var decision = rule.Current!;
        Assert.Equal("stop-difference", decision.State);
        Assert.Equal(40, decision.Count);
        Assert.Equal(sign * 100, decision.Estimate, 10);
        Assert.True(sign > 0 ? decision.Lower > 0 : decision.Upper < 0);
        Assert.Equal(decision, rule.Current);
    }

    [Fact]
    public void Observe_ZeroMeanInsideMargin_StopsForEquivalence()
    {
        var rule = new ExperimentReplaySequentialStopRule(0.05, 1, 2);
        for (var i = 0; i < 5; i++) rule.Observe(i % 2 == 0 ? 1 : -1);
        Assert.Equal("continue", rule.Current!.State);
        for (var i = 5; i < 40; i++) rule.Observe(i % 2 == 0 ? 1 : -1);

        var decision = rule.Current!;
        Assert.Equal("stop-equivalent", decision.State);
        Assert.Equal(40, decision.Count);
        Assert.Equal(0, decision.Estimate, 10);
        Assert.True(decision.Lower >= -2 && decision.Lower <= 0);
        Assert.True(decision.Upper <= 2 && decision.Upper >= 0);
    }

    [Theory]
    [InlineData(0, "stop-equivalent")]
    [InlineData(0.25, "stop-difference")]
    [InlineData(-0.25, "stop-difference")]
    public void Observe_ConstantSequence_PrefersDifferenceWhenBothApply(double value, string expected)
    {
        var rule = new ExperimentReplaySequentialStopRule(0.05, 1, 2);
        for (var i = 0; i < 5; i++) rule.Observe(value);
        Assert.Equal(expected, rule.Current!.State);
        Assert.Equal(value, rule.Current.Lower);
        Assert.Equal(value, rule.Current.Upper);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Observe_NonfiniteValue_ThrowsWithoutChangingState(double value)
    {
        var rule = new ExperimentReplaySequentialStopRule(0.05, 1, 2);
        var control = new ExperimentReplaySequentialStopRule(0.05, 1, 2);
        var before = rule.Observe(1);
        control.Observe(1);
        Assert.Throws<InvalidDataException>(() => rule.Observe(value));
        Assert.Equal(1, rule.Count);
        Assert.Equal(before, rule.Current);
        Assert.Equal(control.Observe(-1), rule.Observe(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_InvalidMixingVarianceOrMargin_Throws(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperimentReplaySequentialStopRule(0.05, value, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperimentReplaySequentialStopRule(0.05, 1, value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Constructor_InvalidSignificanceLevel_Throws(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperimentReplaySequentialStopRule(value, 1, 2));
    }

    [Fact]
    public void Observe_IdenticalSequences_ReturnsIdenticalDecisionsAtEveryLook()
    {
        double[] values = [1, -2, 3, -4, 5, -6, 7, -8, 9, -10,
            11, -12, 13, -14, 15, -16, 17, -18, 19, -20,
            21, -22, 23, -24, 25, -26, 27, -28, 29, -30];
        var first = new ExperimentReplaySequentialStopRule(0.05, 0.5, 2);
        var second = new ExperimentReplaySequentialStopRule(0.05, 0.5, 2);
        foreach (var value in values) Assert.Equal(first.Observe(value), second.Observe(value));
        Assert.Equal(30, first.Count);
    }

    [Fact]
    public void Observe_EvidenceChanges_RecomputesInsteadOfLatching()
    {
        var rule = new ExperimentReplaySequentialStopRule(0.05, 1, 2);
        for (var i = 0; i < 5; i++) rule.Observe(100);
        Assert.Equal("stop-difference", rule.Current!.State);
        for (var i = 0; i < 5; i++) rule.Observe(-100);
        Assert.Equal("continue", rule.Current!.State);
        Assert.True(rule.Current.Lower < 0 && rule.Current.Upper > 0);
        Assert.Equal(10, rule.Count);
    }
}
