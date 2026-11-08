using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ExecutorSlotDecisionTests
{
    [Fact]
    public void Estimate_PerKMedian_UsesKDividedBySlowdown()
    {
        var samples = ExecutorSlotTestData.Jobs().Concat(ExecutorSlotTestData.Jobs(2, 15, prefix: "two"))
            .Concat(ExecutorSlotTestData.Jobs(3, 25, prefix: "three")).ToArray();
        var result = ExecutorSlotDecision.Decide(samples, ExecutorSlotTestData.Settings, null, 2, false, ExecutorSlotTestData.Now);
        Assert.Equal(1.5, result.Estimates[1].Slowdown!.Value, 10);
        Assert.Equal(2 / 1.5, result.Estimates[1].Throughput!.Value, 10);
        Assert.Equal(2.5, result.Estimates[2].Slowdown!.Value, 10);
        Assert.Equal(3 / 2.5, result.Estimates[2].Throughput!.Value, 10);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Trial_AtEachBound_ChangesExactlyOneSlot(int slots)
    {
        var samples = ExecutorSlotTestData.Jobs().Concat(ExecutorSlotTestData.Jobs(slots, 12, prefix: "current")).ToArray();
        var result = ExecutorSlotDecision.Decide(samples, ExecutorSlotTestData.Settings, null, slots, false, ExecutorSlotTestData.Now);
        Assert.Contains(result.Action, new[] { "trial-up", "trial-down" });
        Assert.InRange(result.ProposedSlots, 1, 4);
        Assert.Equal(1, Math.Abs(result.ProposedSlots - slots));
    }

    [Fact]
    public void Trial_HigherThroughputAndUnchangedFailures_KeepsTarget()
    {
        var trial = ExecutorSlotTestData.StartTrial();
        Assert.Equal("trial-up", trial.Action);
        var result = ExecutorSlotDecision.Decide(ExecutorSlotTestData.CompletedTrial(), ExecutorSlotTestData.Settings,
            trial.NextState, 2, false, ExecutorSlotTestData.Now.AddMinutes(2));
        Assert.Equal("keep", result.Action);
        Assert.Equal(2, result.ProposedSlots);
        Assert.Null(result.NextState.Trial);
        Assert.True(result.ComparisonTrial!.Throughput > result.ComparisonBaseline!.Throughput);
    }

    [Fact]
    public void StableWindows_RepeatedTick_DoesNotCountTheSameJobsTwice()
    {
        var settings = ExecutorSlotTestData.Settings with { StableWindows = 3 };
        var first = ExecutorSlotDecision.Decide(ExecutorSlotTestData.Jobs(), settings, null, 1, false, ExecutorSlotTestData.Now);
        Assert.Equal(1, first.NextState.StableWindows);
        var repeat = ExecutorSlotDecision.Decide(ExecutorSlotTestData.Jobs(), settings, first.NextState, 1, false, ExecutorSlotTestData.Now);
        Assert.Equal("hold", repeat.Action);
        Assert.Equal(1, repeat.NextState.StableWindows);
        var secondJobs = ExecutorSlotTestData.Jobs(prefix: "second", end: ExecutorSlotTestData.Now.AddSeconds(-30));
        var second = ExecutorSlotDecision.Decide(ExecutorSlotTestData.Jobs().Concat(secondJobs).ToArray(), settings,
            repeat.NextState, 1, false, ExecutorSlotTestData.Now);
        Assert.Equal(2, second.NextState.StableWindows);
        var third = ExecutorSlotDecision.Decide(secondJobs.Concat(ExecutorSlotTestData.Jobs(prefix: "third",
            end: ExecutorSlotTestData.Now.AddSeconds(-10))).ToArray(), settings, second.NextState, 1, false, ExecutorSlotTestData.Now);
        Assert.Equal("trial-up", third.Action);
    }

    [Fact]
    public void Estimate_MixedLanes_WeightsByObservedSoloWork()
    {
        var samples = ExecutorSlotTestData.Jobs(lane: "short").Concat(ExecutorSlotTestData.Jobs(2, 20, lane: "short", prefix: "s2"))
            .Concat(ExecutorSlotTestData.Jobs(1, 100, lane: "long", prefix: "l1"))
            .Concat(ExecutorSlotTestData.Jobs(2, 100, lane: "long", prefix: "l2")).ToArray();
        var result = ExecutorSlotDecision.Decide(samples, ExecutorSlotTestData.Settings, null, 2, false, ExecutorSlotTestData.Now);
        Assert.Equal((40d * 2 + 400) / 440, result.Estimates[1].Slowdown!.Value, 10);
        Assert.Equal(2 / ((40d * 2 + 400) / 440), result.Estimates[1].Throughput!.Value, 10);
    }

    [Fact]
    public void Comparison_DifferentLaneMix_ExcludesUnmatchedLane()
    {
        var baseline = ExecutorSlotDecision.Estimate(ExecutorSlotTestData.Jobs(), ExecutorSlotTestData.Jobs(), 1, ExecutorSlotTestData.Settings);
        var trialJobs = ExecutorSlotTestData.Jobs(2, 12).Concat(ExecutorSlotTestData.Jobs(2, 1, lane: "focused-evidence", prefix: "f")).ToArray();
        var solo = ExecutorSlotTestData.Jobs().Concat(ExecutorSlotTestData.Jobs(lane: "focused-evidence", prefix: "fs")).ToArray();
        var trial = ExecutorSlotDecision.Estimate(trialJobs, solo, 2, ExecutorSlotTestData.Settings);
        var pair = ExecutorSlotDecision.Compare(baseline, trial);
        Assert.Equal(2 / 1.2, pair.Trial.Throughput!.Value, 10);
        Assert.Equal("lane-not-in-both-windows", pair.Trial.Lanes.Single(l => l.Lane == "focused-evidence").Exclusion);
        Assert.Equal(pair.Baseline.Lanes.Single(l => l.Lane == "lane").Weight,
            pair.Trial.Lanes.Single(l => l.Lane == "lane").Weight);
    }

    [Fact]
    public void Pin_TrialRecommendation_ReportsWithoutProgressingState()
    {
        var state = new ExecutorSlotControllerState(1, ExecutorSlotTestData.Settings);
        var result = ExecutorSlotDecision.Decide(ExecutorSlotTestData.Jobs(), ExecutorSlotTestData.Settings,
            state, 1, true, ExecutorSlotTestData.Now);
        Assert.Equal("report-only", result.Action);
        Assert.Equal("trial-up", result.UnderlyingAction);
        Assert.Equal(2, result.ProposedSlots);
        Assert.Equal(state, result.NextState);
    }

    [Fact]
    public void Pin_CompletedTrial_ReportsRevertWithoutCompletingTrial()
    {
        var state = ExecutorSlotTestData.StartTrial().NextState;
        var result = ExecutorSlotDecision.Decide(ExecutorSlotTestData.CompletedTrial(failure: "lane-timeout"),
            ExecutorSlotTestData.Settings, state, 2, true, ExecutorSlotTestData.Now.AddMinutes(2));
        Assert.Equal("report-only", result.Action);
        Assert.Equal("revert", result.UnderlyingAction);
        Assert.Equal(1, result.ProposedSlots);
        Assert.Equal(state, result.NextState);
    }

    [Fact]
    public void Evidence_AllMeasuredNeighboursAreWorse_DoesNotTrialThem()
    {
        var samples = ExecutorSlotTestData.Jobs().Concat(ExecutorSlotTestData.Jobs(2, 12, prefix: "two"))
            .Concat(ExecutorSlotTestData.Jobs(3, 40, prefix: "three")).ToArray();
        // Make the current-setting window the most recent, independently of the supplied array order.
        samples = samples.Select(s => s.Concurrency == 2 ? s with { ObservedAt = s.ObservedAt.AddSeconds(20) } : s).ToArray();
        var result = ExecutorSlotDecision.Decide(samples, ExecutorSlotTestData.Settings, null, 2, false, ExecutorSlotTestData.Now);
        Assert.Equal("hold", result.Action);
        Assert.Equal(2, result.ProposedSlots);
        Assert.Equal("no-improving-or-unmeasured-neighbour", result.Reason);
    }
}
