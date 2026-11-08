using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns a temporary host and controller.
public sealed class ExecutorSlotControllerGrowthTests
{
    [Fact]
    public void LargeLookback_SerializesBoundedWatermarkAndOneStateCopy()
    {
        using var small = new ExecutorSlotControllerFixture();
        small.Controller().Evaluate();
        var smallRow = Assert.Single(File.ReadAllLines(small.LedgerPath));
        using var large = new ExecutorSlotControllerFixture();
        large.Samples = ExecutorSlotTestData.Jobs(count: 10000);
        large.Now = large.Now.AddHours(3);
        large.Controller().Evaluate();
        var largeRow = Assert.Single(File.ReadAllLines(large.LedgerPath));
        Assert.True(largeRow.Length <= smallRow.Length + 512,
            $"row grew with lookback jobs: small={smallRow.Length}, large={largeRow.Length}");
        Assert.DoesNotContain("completed_attempt_ids", largeRow);
        Assert.DoesNotContain("next_state", largeRow);
        using var json = JsonDocument.Parse(largeRow);
        var watermark = json.RootElement.GetProperty("controller_state").GetProperty("completed_through");
        Assert.Equal("base9999", watermark.GetProperty("attempt_id").GetString());
        Assert.Equal(10000, json.RootElement.GetProperty("decision").GetProperty("total_samples").GetInt32());
    }

    [Fact]
    public void RepeatedTick_UsesCachedStateWithoutReopeningDecisionHistory()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Settings = fixture.Settings with { StableWindows = 3 };
        var controller = fixture.Controller();
        controller.Evaluate();
        // A second history read would throw. The running controller already owns this state.
        File.AppendAllText(fixture.LedgerPath, "malformed\n");
        fixture.Now = fixture.Now.AddSeconds(60);
        Assert.Contains("action=hold", Assert.Single(controller.Evaluate()));
        Assert.Equal(2, File.ReadAllLines(fixture.LedgerPath).Length);
        var last = JsonSerializer.Deserialize<ExecutorSlotDecisionRecord>(File.ReadAllLines(fixture.LedgerPath)[0],
            ExecutorSlotDecisionLedger.JsonOptions)!;
        Assert.Equal(1, last.ControllerState.StableWindows);
        Assert.Throws<JsonException>(() => ExecutorSlotDecisionLedger.ReadLatest(fixture.LedgerPath));
    }

    [Fact]
    public void LegacyAppliedTrial_RestartMigratesStateWithoutLosingFrozenBaseline()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Controller().Evaluate();
        var row = fixture.Latest;
        var json = JsonNode.Parse(Assert.Single(File.ReadAllLines(fixture.LedgerPath)))!;
        json["version"] = 1;
        var state = JsonSerializer.SerializeToNode(row.ControllerState, ExecutorSlotDecisionLedger.JsonOptions)!;
        state.AsObject().Remove("completed_through");
        state["completed_attempt_ids"] = new JsonArray("base0", "base1", "base2", "base3");
        json["before_state"] = JsonSerializer.SerializeToNode(new ExecutorSlotControllerState(1, fixture.Settings),
            ExecutorSlotDecisionLedger.JsonOptions);
        json.AsObject().Remove("controller_state");
        json["decision"]!["next_state"] = state;
        File.WriteAllText(fixture.LedgerPath, json.ToJsonString() + "\n");
        fixture.Samples = ExecutorSlotTestData.CompletedTrial();
        fixture.Now = fixture.Now.AddMinutes(2);
        fixture.Controller().Evaluate();
        Assert.Equal("keep", fixture.Latest.Decision.Action);
        Assert.Null(fixture.Latest.ControllerState.Trial);
    }
}
