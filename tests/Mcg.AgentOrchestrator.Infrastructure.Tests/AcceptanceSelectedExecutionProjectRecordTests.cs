using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionProjectFixture;

// Parallel-safe: immutable fixture inputs and in-memory dispatch observations.
public sealed class AcceptanceSelectedExecutionProjectRecordTests
{
    [Fact]
    public void Observe_ProjectEntries_AppendWithoutChangingInfrastructureBytes()
    {
        var baseline = AcceptanceSelectedExecutionFixture.Observe(AcceptanceSelectedExecutionFixture.Session(
            AcceptanceSelectedExecutionMode.Enforce));
        var observation = Observe(Session());
        var oldRecord = baseline.SelectedExecution!;
        var record = observation.SelectedExecution!;
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(oldRecord.Lanes),
            JsonSerializer.SerializeToUtf8Bytes(record.Lanes.Take(oldRecord.Lanes.Count)));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(oldRecord with { SelectionMilliseconds = 0 }),
            JsonSerializer.SerializeToUtf8Bytes(record with { Lanes = record.Lanes.Take(oldRecord.Lanes.Count).ToArray(), SelectionMilliseconds = 0 }));
        Assert.Equal(new[] { "cli tests", "owner console tests" }, record.Lanes.Skip(oldRecord.Lanes.Count).Select(lane => lane.Name));
        foreach (var inventory in Inventories)
        {
            var lane = Assert.Single(record.Lanes, item => item.Name == inventory.CheckName);
            Assert.Equal("skipped", lane.Disposition);
            Assert.Empty(lane.SelectedClasses);
            Assert.Equal(inventory.Classes.Select(item => item.FullName).Order(StringComparer.Ordinal), lane.SkippedClasses);
            Assert.Equal(AcceptanceGateLaneSkip.LaneNotSelected, lane.LaneSkipReason);
            Assert.Equal(AcceptanceGateLaneSkip.ClassNotSelected, lane.ClassSkipReason);
            Assert.Contains(observation.Skipped, item => item.Name == inventory.CheckName && item.Reason == AcceptanceGateLaneSkip.LaneNotSelected);
        }
        using var json = JsonDocument.Parse(observation.ToJson());
        var publishedLanes = json.RootElement.GetProperty("selectedExecution").GetProperty("lanes");
        Assert.Equal("owner console tests", publishedLanes[publishedLanes.GetArrayLength() - 1].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observe_NarrowedOrShadow_RecordsClassesAndExistingReasons(bool shadow)
    {
        var session = Session(shadow ? AcceptanceSelectedExecutionMode.Shadow : AcceptanceSelectedExecutionMode.Enforce,
            reached: ["A", "Candidate.CliReached"], guards: ["Candidate.Guard", "Candidate.CliGuard"]);
        var observation = Observe(session);
        var lane = Assert.Single(observation.SelectedExecution!.Lanes, item => item.Name == "cli tests");
        Assert.Equal("narrowed", lane.Disposition);
        Assert.Equal(new[] { "Candidate.CliGuard", "Candidate.CliReached" }, lane.SelectedClasses);
        Assert.Equal(new[] { "Candidate.CliOther" }, lane.SkippedClasses);
        Assert.Null(lane.LaneSkipReason);
        Assert.Equal(shadow ? AcceptanceGateLaneSkip.ShadowClassNotSelected : AcceptanceGateLaneSkip.ClassNotSelected,
            lane.ClassSkipReason);
        Assert.Contains("cli tests", observation.Scheduled);
        var skipped = Assert.Single(observation.SelectedExecution.Lanes, item => item.Name == "owner console tests");
        Assert.Equal(shadow ? AcceptanceGateLaneSkip.ShadowLaneNotSelected : AcceptanceGateLaneSkip.LaneNotSelected,
            skipped.LaneSkipReason);
        if (shadow)
        {
            Assert.Contains("owner console tests", observation.Scheduled);
            Assert.DoesNotContain(observation.Skipped, item => item.Name == "owner console tests");
        }
    }
}
