using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the committed golden is read-only and each save uses its own store.
public sealed class GoalTaskHistoryGoldenDocumentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SavedDocumentMatchesGoldenAndIndependentPreChangeSerializer()
    {
        using var fixture = new Fixture();
        var dispatch = new TaskDispatchSnapshot("fixture", "dispatch", "fixture", new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var task = new TaskSnapshot("00000000000000000000000000000001", "Golden task", AgentRole.Developer,
            WorkTaskStatus.Completed, null, null, null, [], dispatch, null, DispatchHistory: [dispatch]);
        var goal = new GoalSnapshot("aaaaaaaa000000000000000000000000", "Golden history document",
            GoalStatus.Completed, [task], []);
        // These are the options and serialization call from commit 9add9754, independent of the new store.
        var oldOptions = new JsonSerializerOptions { WriteIndented = false };
        oldOptions.Converters.Add(new JsonStringEnumConverter());
        var oracle = JsonSerializer.SerializeToUtf8Bytes(goal, oldOptions);
        var goldenPath = Path.Combine(VerifiedRepositoryRoot.Find(), "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures", "GoalTaskHistory", "golden-goal-document.json");
        var golden = File.ReadAllBytes(goldenPath);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var actual = System.Text.Encoding.UTF8.GetBytes(fixture.RawGoal(goal.Id));
        ReportDifference("oracle", golden, oracle);
        ReportDifference("saved", golden, actual);
        Assert.Equal(golden, oracle);
        Assert.Equal(golden, actual);
    }

    private void ReportDifference(string label, byte[] expected, byte[] actual)
    {
        var offset = 0;
        while (offset < Math.Min(expected.Length, actual.Length) && expected[offset] == actual[offset]) offset++;
        output.WriteLine($"{label}: goldenBytes={expected.Length}; actualBytes={actual.Length}; firstDifference={offset}");
    }

    // This entire test uses only APIs present at 9add9754 so its compatibility oracle can run there unchanged.
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "history-golden", Guid.NewGuid().ToString("N"));
        private string DatabasePath => Path.Combine(_root, "state.db");
        internal SqliteOrchestratorStateRepository Repository { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            StateDbMigrations.EnsureUpToDate(DatabasePath);
            Repository = new(DatabasePath);
        }

        internal string RawGoal(string id)
        {
            using var connection = StateDbConnectionFactory.Open(DatabasePath, StateDbConnectionProfile.QueryOnlyRead);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT snapshot_json FROM goals WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            return (string)command.ExecuteScalar()!;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
