using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable synthetic layout and an injected event sink; no processes.
public sealed class AcceptanceBalancedLanePlanPublicationTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("faulted")]
    [InlineData("cancelled")]
    public void Complete_UnchangedPlan_PreservesSingleSelectionEvent(string outcome)
    {
        var events = new List<AcceptanceGateProgress>();
        var writer = CreateWriter(events);
        var published = writer.Snapshot;
        Assert.Single(events);

        writer.Complete(outcome);

        Assert.Equal(published, writer.Snapshot);
        Assert.Equal(published.FormatEventTarget(), Assert.Single(events).CurrentTarget);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("faulted")]
    [InlineData("cancelled")]
    public void Complete_ObservedCommand_PublishesUpdatedPlan(string outcome)
    {
        var events = new List<AcceptanceGateProgress>();
        var writer = CreateWriter(events);
        writer.RecordCommand("infrastructure tests: Balanced 1", 400, responseFile: true);

        writer.Complete(outcome);

        Assert.Equal(2, events.Count);
        using var initial = JsonDocument.Parse(events[0].CurrentTarget["GATE_LANE_PLAN ".Length..]);
        Assert.Empty(initial.RootElement.GetProperty("balancedLayout").GetProperty("commands").EnumerateArray());
        using var updated = JsonDocument.Parse(events[1].CurrentTarget["GATE_LANE_PLAN ".Length..]);
        var command = Assert.Single(updated.RootElement.GetProperty("balancedLayout")
            .GetProperty("commands").EnumerateArray());
        Assert.Equal("Balanced 1", command.GetProperty("lane").GetString());
        Assert.Equal(400, command.GetProperty("commandLineCharacters").GetInt32());
        Assert.True(command.GetProperty("usesResponseFile").GetBoolean());
    }

    private static AcceptanceBalancedLanePlanWriter CreateWriter(List<AcceptanceGateProgress> events)
    {
        var layout = AcceptanceBalancedLanePacker.Pack([new("Tests.A", null)],
            new Dictionary<string, double>(), new());
        return AcceptanceBalancedLanePlanWriter.Publish(new(AcceptanceGateLanePlan.CurrentSchemaVersion,
            "attempt", ["Balanced 1"], []), null, null, events.Add, TimeProvider.System, "unused", layout);
    }
}
