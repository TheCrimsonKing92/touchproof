using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Private artifacts and a fixed clock keep each loader round trip isolated.
internal sealed class OwnerConsoleGateStatusTestFixture : IDisposable
{
    internal const string GoalId = "c6220d3b";
    internal const string Attempt = "c6220d3b-gate-20261115010000000-0123456789abcdef0123456789abcdef";
    internal const string NextAttempt = "c6220d3b-gate-20261115020000000-0123456789abcdef0123456789abcdef";
    internal static readonly DateTimeOffset Now = new(2026, 11, 15, 1, 10, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-status-" + Guid.NewGuid().ToString("N"));
    private readonly IReadOnlyList<OwnerConsoleGateManifestLane> _manifest;
    internal string PlanPath => Artifact(Attempt);

    internal OwnerConsoleGateStatusTestFixture(IReadOnlyList<OwnerConsoleGateManifestLane>? manifest = null)
    {
        _manifest = manifest ?? [new("A", 100)];
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "manifest.json"), JsonSerializer.Serialize(new
        {
            engine = new { maxConcurrentShards = 1, infrastructureTestLanes = _manifest.Select(row =>
                new { name = row.Name, estimatedSerialSeconds = row.EstimatedSerialSeconds }) }
        }));
    }

    internal void WritePlan(AcceptanceSelectedExecutionRecord? selection = null, string execution = Attempt,
        string? recordAttempt = null, IReadOnlyList<string>? scheduled = null) =>
        File.WriteAllText(Artifact(execution), new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion,
            recordAttempt ?? execution, scheduled ?? _manifest.Select(row => row.Name).ToArray(), [])
            { SelectedExecution = selection }.ToJson());

    internal static AcceptanceSelectedExecutionRecord Decision(string decision, string? reason = null, bool applied = true) =>
        new("Shadow", decision, "unrelated-outcome", reason, [], [], [], [], null, null, null, null, 0, false, applied);

    internal OwnerConductEvent PlanEvent(int age = -600, string execution = Attempt) => new(Now.AddSeconds(age),
        "gate-progress", GoalId, $"phase=lane-plan execution={execution} members={GoalId} heartbeat={JsonSerializer.Serialize(Artifact(execution))}");

    internal static OwnerConductEvent Progress(int age, string phase, string target, int elapsedMs = 0,
        string extra = "", string execution = Attempt) => new(Now.AddSeconds(age), "gate-progress", GoalId,
        $"PHASE_PROGRESS execution={execution} members={GoalId} phase={phase} target={JsonSerializer.Serialize(target)} elapsed_ms={elapsedMs}{extra}");

    internal OwnerConsoleGateProgress Load(IEnumerable<OwnerConductEvent>? events = null,
        IEnumerable<OwnerConsoleGateDuration>? history = null, string execution = Attempt)
    {
        var log = Path.Combine(_root, "conduct.jsonl");
        var durations = Path.Combine(_root, "durations.jsonl");
        File.WriteAllLines(log, (events ?? []).Prepend(PlanEvent(execution: execution)).Select(item =>
            JsonSerializer.Serialize(new { timestamp = item.Timestamp, eventKind = item.EventKind, goalId = item.GoalId, detail = item.Detail })));
        File.WriteAllLines(durations, (history ?? []).Select(item => JsonSerializer.Serialize(new
            { laneName = item.LaneName, durationSeconds = item.DurationSeconds, recordedAt = item.RecordedAt })));
        var gate = new OwnerConsoleGateProgressLoader(log, Path.Combine(_root, "manifest.json"), durations,
            new OwnerConsoleTestClock { Now = Now }).Load(new Dictionary<string, string>());
        Assert.NotNull(gate);
        Assert.True(gate.Live);
        Assert.Equal(execution, gate.ExecutionId);
        return gate;
    }

    internal static string Board(OwnerConsoleGateProgress gate)
    {
        var goal = Goal();
        return OwnerConsoleStageDescriber.Describe(goal,
            new(OwnerConsoleAttention.GateRunning, null, GoalId), null, Now, gate);
    }

    internal static string Detail(OwnerConsoleGateProgress gate)
    {
        var output = new OwnerConsoleHarness.FakeOutput();
        OwnerConsoleGoalDetailFormatter.Render(new(Goal(), null, [], gate), output, TimeZoneInfo.Utc);
        return output.Text;
    }

    private static Goal Goal() => new(new(GoalId), "Gate fixture", [new(new("fixture-task"), "Work", AgentRole.Developer)]);
    private string Artifact(string execution) => Path.Combine(_root, execution + AcceptanceGateLanePlan.ArtifactSuffix);
    public void Dispose() => Directory.Delete(_root, true);
}
