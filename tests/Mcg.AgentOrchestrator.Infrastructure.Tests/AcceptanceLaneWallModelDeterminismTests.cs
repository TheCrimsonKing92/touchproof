using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure inputs, no process or environment state.
public sealed class AcceptanceLaneWallModelDeterminismTests
{
    [Fact]
    public void Pack_ShuffledInputs_ProduceIdenticalMeasuredLayout()
    {
        AcceptanceTestClassDescriptor[] classes = [new("A", null), new("B", null), new("C", "serial"), new("D", "serial")];
        var inputs = new Dictionary<string, AcceptanceLaneWallModel.Input>
            { ["A"] = new(90, 90, 2), ["B"] = new(120, 0, 2), ["C"] = new(10, null, 2), ["D"] = new(20, 0, 2) };
        var seconds = inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Seconds);
        var expected = JsonSerializer.Serialize(AcceptanceBalancedLanePacker.Pack(classes, seconds, new() { LaneCount = 3 }, inputs));
        var random = new Random(1701);
        for (var attempt = 0; attempt < 30; attempt++)
            Assert.Equal(expected, JsonSerializer.Serialize(AcceptanceBalancedLanePacker.Pack(
                classes.OrderBy(_ => random.Next()).ToArray(), seconds.Reverse().ToDictionary(), new() { LaneCount = 3 }, inputs.Reverse().ToDictionary())));
    }

    [Fact]
    public void Pack_IndependentUnknownAdded_KnownOwnersStayFixedEvenWhenLaneCountGrows()
    {
        AcceptanceTestClassDescriptor[] classes = [new("A", null), new("B", null)];
        var inputs = new Dictionary<string, AcceptanceLaneWallModel.Input> { ["A"] = new(120, 0, 2), ["B"] = new(90, 90, 2) };
        var seconds = inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Seconds);
        var settings = new AcceptanceBalancedLanePoolSettings { LaneCount = 4 };
        var before = AcceptanceBalancedLanePacker.Pack(classes, seconds, settings, inputs);
        var after = AcceptanceBalancedLanePacker.Pack([new("Unknown", null), .. classes.Reverse()], seconds, settings, inputs);
        foreach (var item in classes)
            Assert.Equal(before.Lanes.Single(lane => lane.Members.Contains(item.FullName)).Name,
                after.Lanes.Single(lane => lane.Members.Contains(item.FullName)).Name);
        Assert.Equal(105, after.DefaultClassSeconds);
        Assert.Equal("Balanced 3", after.Lanes.Single(lane => lane.Members.Contains("Unknown")).Name);
    }
}
