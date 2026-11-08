using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class ExecutorSlotControllerFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "executor-slots-" + Guid.NewGuid().ToString("n"));
    internal string ConfigurationPath => Path.Combine(Root, ".orchestrator", "remote-lane-executors.json");
    internal string EventsPath => Path.Combine(Root, ".orchestrator", "conduct-events.jsonl");
    internal string LedgerPath => Path.Combine(Root, ".orchestrator", ExecutorSlotDecisionLedger.FileName);
    internal DateTimeOffset Now = ExecutorSlotTestData.Now;
    internal IReadOnlyList<ExecutorSlotSample> Samples = ExecutorSlotTestData.Jobs();
    internal bool Pinned;
    internal ExecutorSlotControllerSettings Settings = ExecutorSlotTestData.Settings;

    internal ExecutorSlotControllerFixture()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
        File.WriteAllText(ConfigurationPath, """
            { "executors": [{ "id": "exec", "slots": 1 }], "lanes": ["*"] }
            """);
        Assert.True(RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(ConfigurationPath).Enabled);
    }

    internal ExecutorSlotController Controller(Action<string, ExecutorSlotDecisionRecord>? append = null,
        Func<ExecutorSlotDecisionRecord, bool>? eventWriter = null) => new(Root, ConfigurationPath,
            new ExecutorSettingIntentHandler(ConfigurationPath, EventsPath), new ConductEventLogWriter(EventsPath),
            () => Settings, _ => Pinned, () => Now, (_, _, _) => Samples, append, eventWriter);

    internal ExecutorSlotDecisionRecord Latest => ExecutorSlotDecisionLedger.ReadLatest(LedgerPath)["exec"];
    internal int Slots => RemoteLaneExecutorConfiguration.LoadExecutors(ConfigurationPath).Single().Slots;
    internal string[] Events => File.Exists(EventsPath) ? File.ReadAllLines(EventsPath) : [];
    internal string[] DecisionEvents => Events.Where(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("eventKind").GetString() == "executor-slot-decision";
    }).ToArray();

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
