using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ExecutorSlotDecisionNegativeControlTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Samples_TooFew_HoldsCurrentValue(int count)
    {
        var result = ExecutorSlotDecision.Decide(ExecutorSlotTestData.Jobs(count: count), ExecutorSlotTestData.Settings,
            null, 1, false, ExecutorSlotTestData.Now);
        Assert.Equal("hold", result.Action);
        Assert.Equal(1, result.ProposedSlots);
    }

    [Theory]
    [InlineData("lane-timeout")]
    [InlineData("lease-expired")]
    [InlineData("trx-incomplete")]
    public void Trial_OneFailureRateRises_RevertsDespiteBetterThroughput(string failure)
    {
        var result = ExecutorSlotDecision.Decide(ExecutorSlotTestData.CompletedTrial(failure: failure), ExecutorSlotTestData.Settings,
            ExecutorSlotTestData.StartTrial().NextState, 2, false, ExecutorSlotTestData.Now.AddMinutes(2));
        Assert.True(result.ComparisonTrial!.Throughput > result.ComparisonBaseline!.Throughput);
        Assert.Equal("revert", result.Action);
        Assert.Equal(1, result.ProposedSlots);
    }

    [Fact]
    public void Trial_EqualThroughput_Reverts()
    {
        var result = ExecutorSlotDecision.Decide(ExecutorSlotTestData.CompletedTrial(20), ExecutorSlotTestData.Settings,
            ExecutorSlotTestData.StartTrial().NextState, 2, false, ExecutorSlotTestData.Now.AddMinutes(2));
        Assert.Equal(result.ComparisonBaseline!.Throughput, result.ComparisonTrial!.Throughput);
        Assert.Equal("revert", result.Action);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(16)]
    public void CurrentSlots_OutsideControllerBounds_Holds(int slots)
    {
        var result = ExecutorSlotDecision.Decide(ExecutorSlotTestData.Jobs(), ExecutorSlotTestData.Settings,
            null, slots, false, ExecutorSlotTestData.Now);
        Assert.Equal("hold", result.Action);
        Assert.Equal(slots, result.ProposedSlots);
    }

    [Fact]
    public void Trial_TooFewTargetSamples_HoldsWithoutEndingTrial()
    {
        var jobs = ExecutorSlotTestData.CompletedTrial().Select(s => s with { Concurrency = 1 }).ToArray();
        var result = ExecutorSlotDecision.Decide(jobs, ExecutorSlotTestData.Settings,
            ExecutorSlotTestData.StartTrial().NextState, 2, false, ExecutorSlotTestData.Now.AddMinutes(2));
        Assert.Equal("hold", result.Action);
        Assert.Equal(2, result.ProposedSlots);
        Assert.NotNull(result.NextState.Trial);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Durations_Invalid_HoldsWithoutFabricatingEstimate(double seconds)
    {
        var result = ExecutorSlotDecision.Decide(ExecutorSlotTestData.Jobs(seconds: seconds), ExecutorSlotTestData.Settings,
            null, 1, false, ExecutorSlotTestData.Now);
        Assert.Equal("hold", result.Action);
        Assert.Null(result.Estimates[0].Throughput);
        Assert.Equal(1, result.ProposedSlots);
    }

    [Fact]
    public void OperatorChange_DuringTrial_DiscardsOldRevert()
    {
        var result = ExecutorSlotDecision.Decide(ExecutorSlotTestData.CompletedTrial(failure: "lane-timeout"), ExecutorSlotTestData.Settings,
            ExecutorSlotTestData.StartTrial().NextState, 3, false, ExecutorSlotTestData.Now.AddMinutes(2));
        Assert.Equal("hold", result.Action);
        Assert.Equal(3, result.ProposedSlots);
        Assert.Null(result.NextState.Trial);
        Assert.Equal(0, result.NextState.StableWindows);
    }
}
