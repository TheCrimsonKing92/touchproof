using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: uniquely owned files and injected event sink, no external processes.
public sealed class AcceptanceBalancedLaneHistoryFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "history-failure-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StoreFailures_AreRecordedInThePublishedLanePlan()
    {
        var history = AcceptanceClassDurationHistoryStore.ResolveStorePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        File.WriteAllText(history, "{broken");
        var medians = AcceptanceClassDurationHistoryStore.ReadMedians(_root, 5, out var readFailure);
        Assert.Equal("class-history-read:JsonException", readFailure);
        var layout = AcceptanceBalancedLanePacker.Pack([new("Tests.A", null)], medians, new()) with
            { HistoryReadFailure = readFailure };
        var prefix = Path.Combine(_root, "attempt");
        var events = new List<AcceptanceGateProgress>();
        var writer = AcceptanceBalancedLanePlanWriter.Publish(new(AcceptanceGateLanePlan.CurrentSchemaVersion,
            "attempt", ["Balanced 1"], []), prefix, null, events.Add, TimeProvider.System, _root, layout);
        var trx = Path.Combine(_root, "sample.trx");
        File.WriteAllText(trx, """
            <TestRun><TestDefinitions><UnitTest id="a"><TestMethod className="Tests.A" name="Example" /></UnitTest></TestDefinitions>
            <Results><UnitTestResult testId="a" outcome="Passed" duration="00:00:01" /></Results>
            <ResultSummary outcome="Completed"><Counters total="1" executed="1" notExecuted="0" /></ResultSummary></TestRun>
            """);
        var check = new Check { Name = "infrastructure tests: Balanced 1",
            Project = AcceptancePolicyShardPlanner.InfrastructureTestsProject, IsResolvedInfrastructureLane = true };
        writer.Record(check, new(check.Name, true, 0, null, TestResultPaths: [trx]));
        writer.Complete("completed");

        Assert.Equal("class-history-write:JsonException", writer.Snapshot.BalancedLayout!.HistoryWriteFailure);
        Assert.Equal(2, events.Count);
        using var plan = JsonDocument.Parse(File.ReadAllText(prefix + AcceptanceGateLanePlan.ArtifactSuffix));
        var saved = plan.RootElement.GetProperty("balancedLayout");
        Assert.Equal(readFailure, saved.GetProperty("historyReadFailure").GetString());
        Assert.Equal("class-history-write:JsonException", saved.GetProperty("historyWriteFailure").GetString());
        Assert.Equal("{broken", File.ReadAllText(history));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
