using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ExecutorWholeGateTrialComparisonTests
{
    [Fact]
    public void PrintsRatioAndOnlyTheLaneWithDifferentCounts()
    {
        var receipt = Receipt([Lane("B", 9, 1), Lane("A", 10, 0)], [Lane("A", 10, 0), Lane("B", 9, 0)]);

        Assert.Equal("TRIAL compare executor/local=1.25 parity=different lanes=[B]",
            ExecutorWholeGateTrialComparison.Format(receipt));
    }

    [Fact]
    public void SameCountsAndSameLanesHaveIdenticalParity()
    {
        var receipt = Receipt([Lane("B", 9, 1), Lane("A", 10, 0)], [Lane("A", 10, 0), Lane("B", 9, 1)]);

        Assert.Equal("TRIAL compare executor/local=1.25 parity=identical",
            ExecutorWholeGateTrialComparison.Format(receipt));
    }

    [Fact]
    public void MissingExtraAndExecutionFailedLanesAreSorted()
    {
        var receipt = Receipt([Lane("Z", 1, 0), Lane("B", 1, 0) with { ExecutionFailure = "timeout" }],
            [Lane("B", 1, 0), Lane("A", 1, 0)]);

        Assert.Equal("TRIAL compare executor/local=1.25 parity=different lanes=[A, B, Z]",
            ExecutorWholeGateTrialComparison.Format(receipt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void MissingOrZeroBaselineDurationMakesRatioAndParityUnavailable(int? seconds)
    {
        var receipt = Receipt([], []);
        Assert.Equal("TRIAL compare local attempt missing parity=unavailable",
            ExecutorWholeGateTrialComparison.Format(receipt with { LocalAttempt = null }));
        Assert.Equal("TRIAL compare local attempt missing parity=unavailable",
            ExecutorWholeGateTrialComparison.Format(receipt with
            {
                LocalAttempt = receipt.LocalAttempt! with { WallSeconds = seconds }
            }));
    }

    private static ExecutorWholeGateTrialReceipt Receipt(ExecutorWholeGateTrialLaneResult[] remote,
        ExecutorWholeGateTrialLaneResult[] local) => new("receipt", "goal", "candidate", "executor",
            DateTimeOffset.UnixEpoch, 1500, ExecutorWholeGateTrialStatus.Complete, remote,
            ExecutorWholeGateTrialMemory.Unavailable("not-sampled"),
            new("local", "candidate", 1200, local), []);

    private static ExecutorWholeGateTrialLaneResult Lane(string name, int passed, int failed) =>
        new(name, 20, passed, failed, []);
}
