using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure state/evidence tests.
public sealed class ExecutorSlotWatermarkTests
{
    [Fact]
    public void EqualTimestamps_OnlyLaterAttemptIdsFormANewWindow()
    {
        var settings = ExecutorSlotTestData.Settings with { StableWindows = 3 };
        var samples = ExecutorSlotTestData.Jobs().Select(s => s with { ObservedAt = ExecutorSlotTestData.Now }).ToArray();
        var first = ExecutorSlotDecision.Decide(samples, settings, null, 1, false, ExecutorSlotTestData.Now);
        Assert.Equal(1, first.NextState.StableWindows);
        var later = samples.Concat(samples.Select(s => s with { AttemptId = "later-" + s.AttemptId })).ToArray();
        var second = ExecutorSlotDecision.Decide(later, settings, first.NextState, 1, false, ExecutorSlotTestData.Now);
        Assert.Equal(2, second.NextState.StableWindows);
        var repeat = ExecutorSlotDecision.Decide(later, settings, second.NextState, 1, false, ExecutorSlotTestData.Now);
        Assert.Equal(2, repeat.NextState.StableWindows);
        Assert.Equal("insufficient-new-jobs", repeat.Reason);
        Assert.Equal("later-base3", repeat.NextState.CompletedThrough!.AttemptId);
    }
}
