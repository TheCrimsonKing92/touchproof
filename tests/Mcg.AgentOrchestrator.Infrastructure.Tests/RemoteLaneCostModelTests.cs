using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneCostModelTests
{
    [Fact]
    public void UsesMedianOfSuccessfulTestPhaseRatiosForSameLaneAndExecutor()
    {
        var sample = RemoteLaneCostTestFixture.Sample("A", 190, 100);
        var model = RemoteLaneCostModel.Build([
            sample, sample with { RemoteTestHostCpuSeconds = 300 }, sample with { RemoteTestHostCpuSeconds = 80 },
            sample with { ExecutorId = "other", RemoteTestHostCpuSeconds = 10000 },
            RemoteLaneCostTestFixture.Sample("B", 10000),
            sample with { Outcome = RemoteLaneOutcomeCode.RemoteRed, RemoteTestHostCpuSeconds = 10000 },
            sample with { Outcome = RemoteLaneOutcomeCode.LaneTimeout, RemoteTestHostCpuSeconds = 10000 },
            sample with { Outcome = RemoteLaneOutcomeCode.TrxIncomplete },
            sample with { RemoteTestHostCpuSeconds = double.NaN },
            sample with { RemoteTestHostCpuSeconds = double.PositiveInfinity },
            sample with { RemoteTestHostCpuSeconds = -1 }, sample with { RemoteTestHostCpuSeconds = null },
            sample with { Phases = new(TestSeconds: 0) }, sample with { Phases = null },
            sample with { Phases = new(TestSeconds: double.PositiveInfinity) }
        ]);
        Assert.Equal((1.9, 3), model.Estimate(RemoteLaneCostTestFixture.Lane("A").Name, "executor"));
        Assert.Equal((2.0, 0), model.Estimate("unsampled", "executor"));
    }

    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(10, 0.5)]
    [InlineData(600, 6)]
    public void MeasuredCostHasMinimumAndNoUpperClamp(double cpu, double expected)
    {
        var model = RemoteLaneCostModel.Build([RemoteLaneCostTestFixture.Sample("A", cpu)]);
        Assert.Equal((expected, 1), model.Estimate(RemoteLaneCostTestFixture.Lane("A").Name, "executor"));
    }

    [Fact]
    public void EvenSampleCountUsesMiddlePairAndMissingHistoryUsesDefault()
    {
        var model = RemoteLaneCostModel.Build([RemoteLaneCostTestFixture.Sample("A", 100), RemoteLaneCostTestFixture.Sample("A", 300)]);
        Assert.Equal((2.0, 2), model.Estimate(RemoteLaneCostTestFixture.Lane("A").Name, "executor"));
        Assert.Equal((2.0, 0), model.Estimate(RemoteLaneCostTestFixture.Lane("A").Name, "other"));
        Assert.Equal((2.0, 0), RemoteLaneCostModel.Build([]).Estimate(null, "executor"));
    }
}
