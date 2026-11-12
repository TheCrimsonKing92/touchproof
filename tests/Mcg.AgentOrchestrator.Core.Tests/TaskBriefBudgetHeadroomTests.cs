using Mcg.AgentOrchestrator.Core;

public sealed class TaskBriefBudgetHeadroomTests
{
    [Xunit.Fact]
    public void ExactlyNinetyPercentIsNearLimit()
    {
        var usage = new TaskBriefBudgetHeadroom(10000, 9000);

        Assert.True(usage.IsNearLimit);
        Assert.Equal(1000L, usage.Headroom);
        Assert.Equal(90m, usage.UsedPercentage);
    }

    [Xunit.Fact]
    public void OneCharacterBelowNinetyPercentIsNotNearLimit()
    {
        var usage = new TaskBriefBudgetHeadroom(10000, 8999);

        Assert.False(usage.IsNearLimit);
        Assert.Equal(1001L, usage.Headroom);
        Assert.Equal(89.99m, usage.UsedPercentage);
    }

    [Xunit.Fact]
    public void NonDivisibleBudgetUsesExactThreshold()
    {
        Assert.False(new TaskBriefBudgetHeadroom(9505, 8554).IsNearLimit);
        Assert.True(new TaskBriefBudgetHeadroom(9505, 8555).IsNearLimit);
    }

    [Xunit.Fact]
    public void OverBudgetIsNearLimitAndRetainsNegativeHeadroom()
    {
        var usage = new TaskBriefBudgetHeadroom(8000, 9100);

        Assert.True(usage.IsNearLimit);
        Assert.Equal(-1100L, usage.Headroom);
        Assert.Equal(113.75m, usage.UsedPercentage);
    }

    [Xunit.Fact]
    public void ZeroBudgetHasFinitePercentageWithoutThrowing()
    {
        var empty = new TaskBriefBudgetHeadroom(0, 0);
        var used = new TaskBriefBudgetHeadroom(0, 120);

        Assert.Equal(0m, empty.UsedPercentage);
        Assert.Equal(0L, empty.Headroom);
        Assert.True(empty.IsNearLimit);
        Assert.Equal(0m, used.UsedPercentage);
        Assert.Equal(-120L, used.Headroom);
        Assert.True(used.IsNearLimit);
    }

    [Xunit.Fact]
    public void NegativeBudgetHasFinitePercentageWithoutThrowing()
    {
        var usage = new TaskBriefBudgetHeadroom(-1, 5);

        Assert.Equal(0m, usage.UsedPercentage);
        Assert.Equal(-6L, usage.Headroom);
        Assert.True(usage.IsNearLimit);
    }

    [Xunit.Fact]
    public void NegativeMeasuredCountKeepsSignedArithmetic()
    {
        var usage = new TaskBriefBudgetHeadroom(100, -10);

        Assert.Equal(110L, usage.Headroom);
        Assert.Equal(-10m, usage.UsedPercentage);
        Assert.False(usage.IsNearLimit);
        Assert.True(new TaskBriefBudgetHeadroom(-100, -95).IsNearLimit);
    }

    [Xunit.Fact]
    public void ExtremeInputsUseWidenedArithmetic()
    {
        var usage = new TaskBriefBudgetHeadroom(int.MinValue, int.MaxValue);
        var unused = new TaskBriefBudgetHeadroom(int.MaxValue, int.MinValue);

        Assert.Equal(-4294967295L, usage.Headroom);
        Assert.True(usage.IsNearLimit);
        Assert.Equal(4294967295L, unused.Headroom);
        Assert.True(unused.UsedPercentage < -100m);
        Assert.False(unused.IsNearLimit);
        Assert.True(new TaskBriefBudgetHeadroom(int.MaxValue, int.MaxValue).IsNearLimit);
    }
}
