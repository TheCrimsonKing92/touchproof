// Parallel-safe: private artifacts, explicit paths and a fixed clock.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerConsoleGateSlotAttemptPlanTests : IDisposable
{
    private const string Attempt = "41a09434-0-20261105131307015-97f069e446d6477bb6d563c41853a816";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 13, 31, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "slot gate plan " + Guid.NewGuid().ToString("N"));

    public OwnerConsoleGateSlotAttemptPlanTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SlotAttempt_HeartbeatOnly_FindsAllScheduledLanes()
    {
        var scheduled = Enumerable.Range(1, 26).Select(index => $"infrastructure-tests-balanced-{index}").ToArray();
        var artifact = Path.Combine(_root, Attempt + AcceptanceGateLanePlan.ArtifactSuffix);
        File.WriteAllText(artifact, new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion,
            Attempt, scheduled, [new("build-system", "no-build-system-change")]).ToJson());
        var manifest = Path.Combine(_root, "manifest.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { engine = new {
            maxConcurrentShards = 2,
            infrastructureTestLanes = scheduled.Select(name => new { name, estimatedSerialSeconds = 60 }) } }));
        var log = Path.Combine(_root, "conduct.jsonl");
        var events = new[] {
            Event(Now.AddMinutes(-18), "phase=execution-admitted"),
            Progress(scheduled[0], "b8f1491d4b8bd724"),
            Progress(scheduled[1], "bbf1491d4b8bd725") };
        File.WriteAllLines(log, events.Select(item => JsonSerializer.Serialize(new {
            timestamp = item.Timestamp, eventKind = item.EventKind, goalId = item.GoalId, detail = item.Detail })));

        var gate = Assert.IsType<OwnerConsoleGateProgress>(new OwnerConsoleGateProgressLoader(log, manifest,
            Path.Combine(_root, "durations.jsonl"), new OwnerConsoleTestClock { Now = Now })
            .Load(new Dictionary<string, string>()));

        Assert.DoesNotContain(events, item => item.Detail.Contains("phase=lane-plan", StringComparison.Ordinal));
        Assert.Equal(Attempt, gate.ExecutionId);
        Assert.True(gate.Live);
        Assert.True(gate.LanePlanAvailable);
        Assert.True(gate.ScheduleKnown);
        Assert.True(gate.HasEta);
        Assert.Equal(26, gate.TotalCount);
        Assert.Equal(26, OwnerConsoleGateCount.Of(gate).Total);
        Assert.Equal(scheduled.Order(), gate.Lanes.Where(row => row.IsScheduled == true).Select(row => row.Name).Order());
        var skipped = Assert.Single(gate.Lanes, row => row.Name == "build-system");
        Assert.Equal(OwnerConsoleGateLaneState.Skipped, skipped.State);
        Assert.Equal("no-build-system-change", skipped.SkipReason);
    }

    [Fact]
    public void OtherSlotAttemptHeartbeat_DoesNotSupplyCurrentPlan()
    {
        const string otherAttempt = "41a09434-1-20261105131307015-97f069e446d6477bb6d563c41853a816";
        File.WriteAllText(Path.Combine(_root, Attempt + AcceptanceGateLanePlan.ArtifactSuffix),
            new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, Attempt, ["Console"], []).ToJson());
        var heartbeat = Path.Combine(_root, otherAttempt + ".console.gate-heartbeat.json");

        var gate = OwnerConsoleGateProgress.Build(Now,
            [Event(Now.AddSeconds(-1), $"phase=verification-check target=Console heartbeat={JsonSerializer.Serialize(heartbeat)}")],
            [new("Console", 60)], [], planReader: new OwnerConsoleGateLanePlanReader());

        Assert.Equal(Attempt, gate.ExecutionId);
        Assert.False(gate.LanePlanAvailable);
        Assert.False(gate.ScheduleKnown);
    }

    private OwnerConductEvent Progress(string lane, string hash) => Event(Now.AddSeconds(-10),
        $"phase=verification-check target={lane} heartbeat={JsonSerializer.Serialize(Path.Combine(_root, Attempt + "." + lane + "-" + hash + ".gate-heartbeat.json"))}");
    private static OwnerConductEvent Event(DateTimeOffset at, string fields) => new(at, "gate-progress", "41a09434",
        $"PHASE_PROGRESS goal=41a09434 {fields} execution={Attempt}");
    public void Dispose() => Directory.Delete(_root, true);
}
