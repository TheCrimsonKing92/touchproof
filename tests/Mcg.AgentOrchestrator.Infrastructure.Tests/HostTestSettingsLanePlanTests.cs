using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static HostTestSettingsFixture;

// Parallel-safe: private files and async-flow-scoped accountant; no process-global changes.
public sealed class HostTestSettingsLanePlanTests
{
    [Fact]
    public void LaunchedBindings_AppearInFinalPlanWithTheirOwnHostAndEffectiveMap()
    {
        using var fixture = new HostTestSettingsFixture();
        var config = fixture.Load("""
            {"executors":[{"id":"one","testSettings":{"MCG_TEST_HOST_CAPACITY_SLOTS":"2"}}],
             "lanes":["remote","local"],"localTestSettings":{"MCG_TEST_HOST_CAPACITY_SLOTS":"3"}}
            """);
        var settings = AcceptanceHostTestSettings.Resolve(Manifest.DeclaredTestSettings, config);
        var events = new List<AcceptanceGateProgress>();
        var prefix = Path.Combine(fixture.Root, "attempt");
        using var accountant = AcceptanceGatePhaseAccountant.Start(TimeProvider.System, "goal", _ => { });
        AcceptanceGatePhaseAccountant.SetCurrentHostTestSettings(settings);
        var writer = AcceptanceBalancedLanePlanWriter.Publish(
            new(AcceptanceGateLanePlan.CurrentSchemaVersion, "attempt", ["remote", "local"], []), prefix,
            null, events.Add, TimeProvider.System, fixture.Root, null);
        accountant.BalancedPlan = writer;
        // The same recording seam is used after successful remote submission and local process start.
        AcceptanceGatePhaseAccountant.RecordCurrentTestSettings(settings.ForExecutor("remote", "one"));
        AcceptanceGatePhaseAccountant.RecordCurrentTestSettings(settings.ForLocal("local"));
        File.WriteAllText(fixture.ConfigurationPath, "{}");
        writer.Complete("completed");
        Assert.Equal(2, events.Count);
        var json = File.ReadAllText(prefix + AcceptanceGateLanePlan.ArtifactSuffix);
        Assert.Equal("GATE_LANE_PLAN " + json, events[^1].CurrentTarget);
        var plan = JsonSerializer.Deserialize<AcceptanceGateLanePlan>(json, AcceptanceGateLanePlan.JsonOptions)!;
        Assert.Equal(2, plan.LaneTestSettings.Count);
        Assert.Equal("remote", plan.LaneTestSettings[0].Lane);
        Assert.Equal("one", plan.LaneTestSettings[0].Host);
        Assert.Equal("2", plan.LaneTestSettings[0].Settings[Slots]);
        Assert.Equal("local", plan.LaneTestSettings[1].Lane);
        Assert.Equal("local", plan.LaneTestSettings[1].Host);
        Assert.Equal("3", plan.LaneTestSettings[1].Settings[Slots]);
        Assert.Equal("3", AcceptanceGatePhaseAccountant.CurrentLocalTestSettingsFor("local")!.Settings[Slots]);
        Assert.Null(AcceptanceGatePhaseAccountant.CurrentLocalTestSettingsFor("unlaunched"));
    }

    [Fact]
    public void NoLaunch_PreservesSingleEventAndLegacyPlanDefaults()
    {
        using var fixture = new HostTestSettingsFixture();
        var events = new List<AcceptanceGateProgress>();
        var writer = AcceptanceBalancedLanePlanWriter.Publish(
            new(AcceptanceGateLanePlan.CurrentSchemaVersion, "attempt", ["lane"], []), null,
            null, events.Add, TimeProvider.System, fixture.Root, null);
        writer.Complete("faulted");
        Assert.Single(events);
        Assert.Empty(writer.Snapshot.LaneTestSettings);
        var legacy = JsonSerializer.Deserialize<AcceptanceGateLanePlan>(
            """{"schemaVersion":"gate-lane-plan.v1","attemptId":"old","scheduled":[],"skipped":[]}""",
            AcceptanceGateLanePlan.JsonOptions)!;
        Assert.Empty(legacy.LaneTestSettings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SlotTimingReceipt_RecordsLaunchSnapshotIncludingFailedRuns(bool passed)
    {
        using var fixture = new HostTestSettingsFixture();
        var config = fixture.Load("""{"executors":[],"lanes":[],"localTestSettings":{"MCG_TEST_HOST_CAPACITY_SLOTS":"2"}}""");
        var binding = AcceptanceHostTestSettings.Resolve(Manifest.DeclaredTestSettings, config).ForLocal("lane");
        var trx = Path.Combine(fixture.Root, "lane.trx");
        File.WriteAllText(trx, """<TestRun><Results><UnitTestResult testName="Example.Test" outcome="Passed" duration="00:00:05" /></Results></TestRun>""");
        File.WriteAllText(LaneSlotTimingReader.PathFor(trx), """{"test_name":"Example.Test","wait_ms":1,"held_ms":2,"slot_count":2,"process_id":1}""");
        var rows = new List<string>();
        var result = new AcceptanceCheckResult("lane", passed, passed ? 0 : 1, null, TestResultPaths: [trx]);
        GateLaneReceiptLedger.Record(fixture.Root, "goal", "lane", result, TimeSpan.FromSeconds(5),
            DateTimeOffset.UnixEpoch, (path, json) => { if (path.EndsWith(GateLaneReceiptLedger.FileName)) rows.Add(json); }, binding);
        using var row = JsonDocument.Parse(Assert.Single(rows));
        Assert.Equal(2, row.RootElement.GetProperty("slot_count").GetInt32());
        Assert.Equal("2", row.RootElement.GetProperty("test_settings").GetProperty(Slots).GetString());
        Assert.Equal("local", row.RootElement.GetProperty("test_settings_host").GetString());
    }
}
