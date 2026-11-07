using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceLaneTimeoutBudgetTests
{
    [Xunit.Theory]
    [Xunit.InlineData(2068.631, 3103)]
    [Xunit.InlineData(1000, 2400)]
    [Xunit.InlineData(1600, 2400)]
    [Xunit.InlineData(1600.0004, 2401)]
    [Xunit.InlineData(null, 2400)]
    [Xunit.InlineData(0, 2400)]
    [Xunit.InlineData(-5, 2400)]
    [Xunit.InlineData(double.NaN, 2400)]
    [Xunit.InlineData(double.PositiveInfinity, 2400)]
    [Xunit.InlineData(double.NegativeInfinity, 2400)]
    public void Resolve_Estimate_ReturnsLargerRoundedBudget(double? estimate, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds),
            AcceptanceLaneTimeoutBudget.Resolve(TimeSpan.FromSeconds(2400), estimate));
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData(1000)]
    [Xunit.InlineData(1600)]
    public void Resolve_ScalingDoesNotWin_PreservesDefaultTicks(double? estimate)
    {
        var configured = TimeSpan.FromSeconds(2400).Add(TimeSpan.FromTicks(1));

        Assert.Equal(configured, AcceptanceLaneTimeoutBudget.Resolve(configured, estimate));
    }

    [Xunit.Theory]
    [Xunit.InlineData(double.MaxValue)]
    [Xunit.InlineData(1e12)]
    public void Resolve_UnrepresentableBudget_Saturates(double estimate)
    {
        Assert.Equal(TimeSpan.MaxValue,
            AcceptanceLaneTimeoutBudget.Resolve(TimeSpan.FromSeconds(2400), estimate));
    }
}
