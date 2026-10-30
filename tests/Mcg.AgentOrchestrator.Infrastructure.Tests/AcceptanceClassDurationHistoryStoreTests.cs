using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: uniquely owned temporary store, no global route overrides.
public sealed class AcceptanceClassDurationHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "class-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingStore_ReturnsNoHistory_WithoutAReadFailure()
    {
        Assert.Empty(AcceptanceClassDurationHistoryStore.ReadMedians(_root, 5, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("{broken", "JsonException")]
    [InlineData("{\"version\":99,\"classes\":{}}", "InvalidDataException")]
    public void CorruptStore_ReportsReadAndWriteFailures_AndPreservesData(string text, string error)
    {
        var path = AcceptanceClassDurationHistoryStore.ResolveStorePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        Assert.Empty(AcceptanceClassDurationHistoryStore.ReadMedians(_root, 5, out var reason));
        Assert.Equal("class-history-read:" + error, reason);
        Assert.Equal("class-history-write:" + error, AcceptanceClassDurationHistoryStore.RecordAttempt(
            _root, "attempt", [new("attempt", "Balanced 1", "Tests.A", 1)], 5, true, true));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void UnreadableStore_ReportsReadFailure_InsteadOfMissingHistory()
    {
        Directory.CreateDirectory(AcceptanceClassDurationHistoryStore.ResolveStorePath(_root));
        Assert.Empty(AcceptanceClassDurationHistoryStore.ReadMedians(_root, 5, out var reason));
        Assert.StartsWith("class-history-read:", reason);
    }

    [Fact]
    public void CompletedAttempts_KeepBoundedMedians_AndIgnoreFailedOrIncomplete()
    {
        Record("first", 10, 20);
        Record("second", 30, 40);
        Assert.Equal(20, AcceptanceClassDurationHistoryStore.ReadMedians(_root, 2)["Tests.A"]);
        Assert.Equal(30, AcceptanceClassDurationHistoryStore.ReadMedians(_root, 2)["Tests.B"]);
        var packed = AcceptanceBalancedLanePacker.Pack([new("Tests.A", null), new("Tests.B", null)],
            AcceptanceClassDurationHistoryStore.ReadMedians(_root, 2), new() { LaneCount = 1 });
        Assert.Equal(50, Assert.Single(packed.Lanes).PredictedSeconds);
        var original = File.ReadAllText(AcceptanceClassDurationHistoryStore.ResolveStorePath(_root));
        Record("failed", 999, 999, passed: false);
        Record("incomplete", 999, 999, completed: false);
        Record("second", 999, 999);
        Assert.Equal(original, File.ReadAllText(AcceptanceClassDurationHistoryStore.ResolveStorePath(_root)));
        Record("third", 50, 60);
        Assert.Equal(40, AcceptanceClassDurationHistoryStore.ReadMedians(_root, 2)["Tests.A"]);
        using var document = JsonDocument.Parse(File.ReadAllText(AcceptanceClassDurationHistoryStore.ResolveStorePath(_root)));
        Assert.Equal(["second", "third"], document.RootElement.GetProperty("classes").GetProperty("Tests.A")
            .EnumerateArray().Select(row => row.GetProperty("attemptId").GetString()));
    }

    [Fact]
    public void TrxReader_SumsEveryMethod_IncludingSubsecondDurations()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "complete.trx");
        File.WriteAllText(path, """
            <TestRun><TestDefinitions>
              <UnitTest id="a"><TestMethod className="Tests.A" name="First" /></UnitTest>
              <UnitTest id="b"><TestMethod className="Tests.A" name="Second" /></UnitTest>
            </TestDefinitions><Results>
              <UnitTestResult testId="a" outcome="Passed" duration="00:00:00.100" />
              <UnitTestResult testId="b" outcome="Passed" duration="00:00:00.200" />
            </Results><ResultSummary outcome="Completed"><Counters total="2" executed="2" notExecuted="0" /></ResultSummary></TestRun>
            """);
        Assert.Equal(0.3, AcceptanceClassDurationResultReader.Read([path])!["Tests.A"], 6);
        File.WriteAllText(path, File.ReadAllText(path).Replace("executed=\"2\"", "executed=\"1\""));
        Assert.Null(AcceptanceClassDurationResultReader.Read([path]));
    }

    private void Record(string attempt, double first, double second, bool completed = true, bool passed = true) =>
        Assert.Null(AcceptanceClassDurationHistoryStore.RecordAttempt(_root, attempt,
            [new(attempt, "Balanced 1", "Tests.A", first), new(attempt, "Balanced 2", "Tests.B", second)],
            2, completed, passed));

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
