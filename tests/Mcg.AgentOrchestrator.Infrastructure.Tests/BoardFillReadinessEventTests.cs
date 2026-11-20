using System.Text.Json;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each harness owns its board-fill store and conduct log.
public sealed class BoardFillReadinessEventTests
{
    [Fact]
    public void Tick_records_initial_and_changed_counts_once_even_at_the_draft_cap()
    {
        using var harness = new BoardFillTestHarness();
        harness.Policy = harness.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        var first = Blocked('a', "missing1", BacklogDependencyTargetKind.Goal);
        var second = Blocked('b', "missing1", BacklogDependencyTargetKind.Goal);
        var third = Blocked('c', "waiting2", BacklogDependencyTargetKind.Backlog);
        harness.Items = [third, second, first, first];

        harness.Host.ServiceTick(harness.Kernel);
        var initial = Assert.Single(ReadSnapshots(harness.EventsPath));
        Assert.Equal(2, initial.Count);
        Assert.Equal(2, initial[" [Blocked: missing prerequisite missing1]"]);
        Assert.Equal(1, initial[" [Blocked: waiting on open prerequisite waiting2]"]);

        harness.Host.ServiceTick(harness.Kernel);
        Assert.Single(ReadSnapshots(harness.EventsPath));
        var replacement = Blocked('d', "missing1", BacklogDependencyTargetKind.Goal);
        harness.Items = [first, replacement, third];
        harness.Host.ServiceTick(harness.Kernel);
        Assert.Single(ReadSnapshots(harness.EventsPath));

        harness.Policy = harness.Policy with { BoardFillMaxDraftsPerDay = 0 };
        harness.Items = [third, first];
        harness.Host.ServiceTick(harness.Kernel);
        var updated = ReadSnapshots(harness.EventsPath);
        Assert.Equal(2, updated.Count);
        Assert.Equal(2, updated[1].Count);
        Assert.Equal(1, updated[1][" [Blocked: missing prerequisite missing1]"]);
        Assert.Equal(1, updated[1][" [Blocked: waiting on open prerequisite waiting2]"]);

        harness.Items = [];
        harness.Host.ServiceTick(harness.Kernel);
        var cleared = ReadSnapshots(harness.EventsPath);
        Assert.Equal(3, cleared.Count);
        Assert.Empty(cleared[2]);
        Assert.Empty(harness.Store.ReadAll());
    }

    [Fact]
    public void First_tick_reports_an_empty_map_when_there_are_no_candidates()
    {
        using var harness = new BoardFillTestHarness();
        harness.Policy = harness.Policy with { BoardFillMode = ConductorBoardFillMode.Shadow };
        harness.Items = [];

        harness.Host.ServiceTick(harness.Kernel);
        harness.Host.ServiceTick(harness.Kernel);

        Assert.Empty(Assert.Single(ReadSnapshots(harness.EventsPath)));
    }

    private static BacklogItem Blocked(char id, string prerequisite, BacklogDependencyTargetKind kind)
    {
        var item = BoardFillReadyItemSelectorTests.Item(id);
        return item with { Dependencies = [new(item.Id, prerequisite, kind, item.CreatedAt)] };
    }

    private static List<Dictionary<string, int>> ReadSnapshots(string eventPath)
    {
        var snapshots = new List<Dictionary<string, int>>();
        foreach (var line in File.Exists(eventPath) ? File.ReadLines(eventPath) : [])
        {
            using var entry = JsonDocument.Parse(line);
            var root = entry.RootElement;
            if (root.GetProperty("eventKind").GetString() == "BOARD_FILL_READINESS")
                snapshots.Add(JsonSerializer.Deserialize<Dictionary<string, int>>(root.GetProperty("detail").GetString()!)!);
        }
        return snapshots;
    }
}
