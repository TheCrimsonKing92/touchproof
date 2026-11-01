using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every store read/write is under this test instance's unique temporary root.
public sealed class AcceptanceClassDurationHistoryAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "focused-history-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SnapshotUsesCompletedGateMediansAndNeverWritesTheStore()
    {
        Record("first", 10);
        Record("second", 30);
        var path = AcceptanceClassDurationHistoryStore.ResolveStorePath(_root);
        var original = File.ReadAllText(path);
        var history = AcceptanceClassDurationHistoryAdapter.Read(_root);
        Assert.Equal(20, history.PredictSeconds("Infrastructure.Tests:Tests.Alpha"));
        Assert.Equal(original, File.ReadAllText(path));
        Record("third", 100);
        Assert.Equal(20, history.PredictSeconds("Infrastructure.Tests:Tests.Alpha"));
        Assert.Equal(30, AcceptanceClassDurationHistoryAdapter.Read(_root).PredictSeconds("Infrastructure.Tests:Tests.Alpha"));
        Assert.Null(history.PredictSeconds("Core.Tests:Tests.Alpha"));
    }

    [Fact]
    public void ExactNamesWinAndAmbiguousShortNamesStayUnavailable()
    {
        var history = new AcceptanceClassDurationHistoryAdapter(new Dictionary<string, double>
        {
            ["One.Alpha"] = 10, ["Two.Alpha"] = 20, ["Beta"] = 0,
            ["Invalid"] = -1, ["Nonfinite"] = double.NaN
        });
        Assert.Equal(10, history.PredictSeconds("Infrastructure.Tests:One.Alpha"));
        Assert.Null(history.PredictSeconds("Infrastructure.Tests:Other.Alpha"));
        Assert.Equal(0, history.PredictSeconds("Infrastructure.Tests:Namespaced.Beta"));
        Assert.Null(history.PredictSeconds("Infrastructure.Tests:Invalid"));
        Assert.Null(history.PredictSeconds("Infrastructure.Tests:Nonfinite"));
    }

    [Fact]
    public void MissingOrCorruptStoreReportsUnavailable()
    {
        Assert.Null(AcceptanceClassDurationHistoryAdapter.Read(_root).PredictSeconds("Infrastructure.Tests:Tests.Alpha"));
        var path = AcceptanceClassDurationHistoryStore.ResolveStorePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{broken");
        Assert.Null(AcceptanceClassDurationHistoryAdapter.Read(_root).PredictSeconds("Infrastructure.Tests:Tests.Alpha"));
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    private void Record(string attempt, double seconds) => Assert.Null(AcceptanceClassDurationHistoryStore.RecordAttempt(
        _root, attempt, [new(attempt, "Balanced 1", "Tests.Alpha", seconds)], 5, true, true));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
