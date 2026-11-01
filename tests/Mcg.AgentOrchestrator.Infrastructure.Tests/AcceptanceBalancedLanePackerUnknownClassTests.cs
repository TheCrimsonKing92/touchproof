using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure inputs.
public sealed class AcceptanceBalancedLanePackerUnknownClassTests
{
    [Theory]
    [InlineData(100d, 90d)]
    [InlineData(100d, 1d)]
    public void UnknownClass_UsesKnownMedian_AndOnlyAddsItsEstimate(double first, double second)
    {
        AcceptanceTestClassDescriptor[] known = [new("Tests.A", null), new("Tests.B", null)];
        var medians = new Dictionary<string, double> { ["Tests.A"] = first, ["Tests.B"] = second };
        var settings = new AcceptanceBalancedLanePoolSettings { LaneCount = 2 };
        var before = AcceptanceBalancedLanePacker.Pack(known, medians, settings);
        var after = AcceptanceBalancedLanePacker.Pack([new("Tests.Unknown", null), .. known.Reverse()], medians, settings);
        var estimate = (first + second) / 2;
        Assert.Equal(estimate, after.DefaultClassSeconds);
        Assert.Equal(["Tests.B", "Tests.Unknown"], after.Lanes[1].Members);
        Assert.Equal(before.Lanes[0].Members, after.Lanes[0].Members);
        Assert.Equal(second + estimate, after.Lanes[1].PredictedSeconds);
        Assert.InRange(after.Lanes.Max(lane => lane.PredictedSeconds) - before.Lanes.Max(lane => lane.PredictedSeconds), 0, estimate);
    }

    [Fact]
    public void EmptyHistory_UsesConfiguredFallback_WithoutEmptyLanes()
    {
        var layout = AcceptanceBalancedLanePacker.Pack([new("Tests.New", null)], new Dictionary<string, double>(),
            new() { LaneCount = 4, DefaultClassSecondsWhenNoHistory = 3 });
        Assert.Equal(3, layout.DefaultClassSeconds);
        Assert.Equal(3, Assert.Single(layout.Lanes).PredictedSeconds);
        Assert.Empty(AcceptanceBalancedLanePacker.Pack([], new Dictionary<string, double>(), new()).Lanes);
    }
}
