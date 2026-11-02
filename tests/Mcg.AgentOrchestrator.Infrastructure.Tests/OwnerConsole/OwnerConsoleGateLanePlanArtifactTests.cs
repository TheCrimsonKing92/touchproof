// Parallel-safe: private files and a fixed clock; no processes or global state.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerConsoleGateLanePlanArtifactTests : IDisposable
{
    private const string Attempt = "3ad69258-gate-20261102090000000-0123456789abcdef0123456789abcdef";
    private const string NextAttempt = "3ad69258-gate-20261102090100000-0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 5, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate plan artifact " + Guid.NewGuid().ToString("N"));
    private string Artifact => Path.Combine(_root, Attempt + AcceptanceGateLanePlan.ArtifactSuffix);
    private string Heartbeat => Path.Combine(_root, Attempt + ".console-0a1b2c3d.gate-heartbeat.json");
    private string Log => Path.Combine(_root, "conduct.jsonl");
    private string Manifest => Path.Combine(_root, "manifest.json");

    public OwnerConsoleGateLanePlanArtifactTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Manifest, """{"engine":{"maxConcurrentShards":1,"infrastructureTestLanes":[{"name":"Console","estimatedSerialSeconds":10},{"name":"Build","estimatedSerialSeconds":30}]}}""");
    }

    [Fact]
    public void MissingPlanEvent_ValidArtifact_ShowsScheduledAndSkippedLanes()
    {
        WritePlan(Artifact);
        WriteEvents(Progress(Heartbeat));

        var gate = Load();

        Assert.True(gate.Live);
        Assert.True(gate.LanePlanAvailable);
        Assert.Equal(OwnerConsoleGateLaneState.Running, Assert.Single(gate.Lanes, row => row.Name == "Console").State);
        var skipped = Assert.Single(gate.Lanes, row => row.Name == "Build");
        Assert.Equal(OwnerConsoleGateLaneState.Skipped, skipped.State);
        Assert.Equal("no-build-system-change", skipped.SkipReason);
        var lines = OwnerConsoleGateLayout.Lines(gate, 180, false);
        Assert.Contains(lines, line => line.Contains("Console", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("skipped: no-build-system-change", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("lane plan unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingPlanEvent_MissingArtifact_ShowsUnavailable()
    {
        WriteEvents(Progress(Heartbeat));

        AssertUnavailable(Load());
    }

    [Theory]
    [InlineData("wrong-attempt")]
    [InlineData("wrong-schema")]
    [InlineData("malformed")]
    public void MissingPlanEvent_InvalidArtifact_ShowsUnavailable(string fault)
    {
        WritePlan(Artifact, fault == "wrong-attempt" ? NextAttempt : Attempt,
            schema: fault == "wrong-schema" ? "future" : AcceptanceGateLanePlan.CurrentSchemaVersion);
        if (fault == "malformed") File.WriteAllText(Artifact, "{");
        WriteEvents(Progress(Heartbeat));

        AssertUnavailable(Load());
    }

    [Fact]
    public void PlanEventPresent_ConflictingDerivedArtifact_EventWins()
    {
        var eventDirectory = Path.Combine(_root, "event");
        Directory.CreateDirectory(eventDirectory);
        var eventArtifact = Path.Combine(eventDirectory, Attempt + AcceptanceGateLanePlan.ArtifactSuffix);
        WritePlan(eventArtifact, reason: "outside-attribution-scope");
        WritePlan(Artifact);
        WriteEvents(PlanEvent(eventArtifact), Progress(Heartbeat));

        var gate = Load();

        Assert.True(gate.LanePlanAvailable);
        Assert.Equal("outside-attribution-scope", Assert.Single(gate.Lanes, row => row.Name == "Build").SkipReason);
        Assert.DoesNotContain(OwnerConsoleGateLayout.Lines(gate, 180, false),
            line => line.Contains("no-build-system-change", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanEventPresent_UnreadableEventArtifact_DoesNotFallBack(bool malformed)
    {
        WritePlan(Artifact);
        var eventArtifact = Path.Combine(_root, Attempt + ".event.lane-plan.json");
        if (malformed) File.WriteAllText(eventArtifact, "{");
        WriteEvents(PlanEvent(eventArtifact), Progress(Heartbeat));

        AssertUnavailable(Load());
    }

    [Fact]
    public void NewAttempt_MissingArtifact_DoesNotUseEarlierAttemptHeartbeat()
    {
        WritePlan(Artifact, NextAttempt);
        WriteEvents(Progress(Heartbeat, -20), Progress(Path.Combine(_root, NextAttempt + ".gate-heartbeat.json")));

        AssertUnavailable(Load());
    }

    [Fact]
    public void MissingPlanEvent_RelativeHeartbeat_DoesNotGuessArtifactDirectory()
    {
        WritePlan(Artifact);
        WriteEvents(Progress(Path.GetFileName(Heartbeat)));

        Assert.Null(OwnerConsoleGateLanePlanReader.ArtifactPath(Path.GetFileName(Heartbeat), Attempt));
        AssertUnavailable(Load());
    }

    [Fact]
    public void MissingPlanEvent_LastHeartbeatRelative_UsesEarlierRootedHeartbeat()
    {
        WritePlan(Artifact);
        WriteEvents(Progress(Heartbeat, -20), Progress(Path.GetFileName(Heartbeat)));

        var gate = Load();

        Assert.True(gate.LanePlanAvailable);
        Assert.Equal(OwnerConsoleGateLaneState.Skipped, Assert.Single(gate.Lanes, row => row.Name == "Build").State);
    }

    private static void AssertUnavailable(OwnerConsoleGateProgress gate)
    {
        Assert.True(gate.Live);
        Assert.False(gate.LanePlanAvailable);
        Assert.Contains(OwnerConsoleGateLayout.Lines(gate, 180, false),
            line => line.Contains("(lane plan unavailable)", StringComparison.Ordinal));
    }

    private static void WritePlan(string path, string attempt = Attempt, string reason = "no-build-system-change",
        string schema = AcceptanceGateLanePlan.CurrentSchemaVersion) =>
        File.WriteAllText(path, new AcceptanceGateLanePlan(schema, attempt, ["Console"], [new("Build", reason)]).ToJson());
    private static OwnerConductEvent PlanEvent(string path) => new(Now.AddSeconds(-30), "gate-progress", "3ad69258",
        $"phase=lane-plan target=plan heartbeat={JsonSerializer.Serialize(path)}");
    private static OwnerConductEvent Progress(string heartbeat, int secondsAgo = -10) => new(Now.AddSeconds(secondsAgo),
        "gate-progress", "3ad69258", $"phase=verification-check target=Console heartbeat={JsonSerializer.Serialize(heartbeat)}");
    private void WriteEvents(params OwnerConductEvent[] events) => File.WriteAllLines(Log, events.Select(item => JsonSerializer.Serialize(new
    { timestamp = item.Timestamp, eventKind = item.EventKind, goalId = item.GoalId, detail = item.Detail })));
    private OwnerConsoleGateProgress Load() => Assert.IsType<OwnerConsoleGateProgress>(new OwnerConsoleGateProgressLoader(
        Log, Manifest, Path.Combine(_root, "durations.jsonl"), new OwnerConsoleTestClock { Now = Now }).Load(new Dictionary<string, string>()));
    public void Dispose() => Directory.Delete(_root, true);
}
