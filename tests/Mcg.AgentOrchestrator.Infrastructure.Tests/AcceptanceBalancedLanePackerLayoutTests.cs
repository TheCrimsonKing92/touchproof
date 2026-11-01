using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure inputs, no host state.
public sealed class AcceptanceBalancedLanePackerLayoutTests
{
    [Fact]
    public void SixKnownClasses_PackLongestFirst_IndependentOfInputOrder()
    {
        var classes = "ABCDEF".Select(name => new AcceptanceTestClassDescriptor("Tests." + name, null)).ToArray();
        var medians = classes.Select((item, index) => (item.FullName, Seconds: 100d - index * 10))
            .ToDictionary(item => item.FullName, item => item.Seconds);
        var settings = new AcceptanceBalancedLanePoolSettings { LaneCount = 2 };
        var layout = AcceptanceBalancedLanePacker.Pack(classes, medians, settings);
        Assert.Equal(["Balanced 1", "Balanced 2"], layout.Lanes.Select(lane => lane.Name));
        Assert.Equal(["Tests.A", "Tests.D", "Tests.E"], layout.Lanes[0].Members);
        Assert.Equal(["Tests.B", "Tests.C", "Tests.F"], layout.Lanes[1].Members);
        Assert.Equal([230d, 220d], layout.Lanes.Select(lane => lane.PredictedSeconds));
        var random = new Random(1701);
        for (var index = 0; index < 30; index++)
            Assert.Equal(JsonSerializer.Serialize(layout), JsonSerializer.Serialize(
                AcceptanceBalancedLanePacker.Pack(classes.OrderBy(_ => random.Next()).ToArray(), medians, settings)));
    }

    [Fact]
    public void EqualEstimates_UseOrdinalNames_AndKeepCollectionsTogether()
    {
        AcceptanceTestClassDescriptor[] classes = [new("Tests.Z", null), new("Tests.A", null),
            new("Tests.B", "shared"), new("Tests.C", "shared")];
        var medians = classes.ToDictionary(item => item.FullName, _ => 10d);
        var layout = AcceptanceBalancedLanePacker.Pack(classes, medians, new() { LaneCount = 2 });
        Assert.Equal(["Tests.B", "Tests.C"], layout.Lanes[0].Members);
        Assert.Equal(["Tests.A", "Tests.Z"], layout.Lanes[1].Members);
        Assert.All(layout.Lanes, lane => Assert.Equal(20, lane.PredictedSeconds));
    }
}
