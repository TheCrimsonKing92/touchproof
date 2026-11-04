using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Explicit private artifact/permit roots, no build slot or environment mutation.
internal sealed class AcceptanceLaneWaitArtifactFixture : IAsyncDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "lane-wait-" + Guid.NewGuid().ToString("N"));
    internal string DirectoryPath => Path.Combine(Root, ".orchestrator");
    internal const string Id = "12345678-0-20261103000000000-00000000000000000000000000000000";
    internal string Prefix => Path.Combine(DirectoryPath, "acceptance-gate-attempts", "goal", Id);
    internal AcceptanceLaneWaitTestClock Clock { get; } = new();
    internal AcceptanceAttemptExecutionOwner Owner { get; }
    internal ConductorParallelAcceptanceAttempt Attempt { get; }
    internal AcceptanceLaneWaitCollector Collector => Owner.GetLaneWaitCollector(Clock);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal AcceptanceLaneWaitArtifactFixture()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Prefix)!);
        Attempt = new(Id, "goal", "goal", 0, "commit", "main", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, Environment.ProcessId, ConductorParallelAcceptanceAttemptOutcome.Running,
            Prefix + ".out.log", Prefix + ".err.log", Prefix + ".exit.txt", Prefix + ".heartbeat.json",
            Prefix + ".result.json", Prefix + ".attempt.json");
        File.WriteAllText(Attempt.MetadataPath, JsonSerializer.Serialize(Attempt, Json));
        Owner = new(new(Id, "goal", Root, "tree", "main", "commit", Prefix, 0, Environment.ProcessId, null),
            new AcceptanceGateEngineSettings(), options: new(LaneWaitSink: rows =>
                ConductorParallelAcceptanceAttemptCoordinator.PersistLaneWaits(Attempt, rows)));
    }

    internal void Write(params AcceptanceLaneWaitOccurrence[] rows) => File.WriteAllText(Attempt.MetadataPath,
        JsonSerializer.Serialize(Attempt with { LaneWaits = rows }, Json));
    internal GateStatsWaitsReport.Report Report(int last = 10, int top = 20) => GateStatsReport.Waits(DirectoryPath, last, top);
    public async ValueTask DisposeAsync()
    {
        await Owner.DisposeAsync();
        Directory.Delete(Root, recursive: true);
    }
}
