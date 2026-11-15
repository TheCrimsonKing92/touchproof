using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the committed golden is read-only and each save uses its own store.
public sealed class GoalTaskHistoryGoldenDocumentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task InlineGoldenLoadsUnchangedAndConvertsToReadableMarkedDocument()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
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
        ReportDifference("oracle", golden, oracle);
        Assert.Equal(golden, oracle);
        fixture.InsertLegacy(goal);
        fixture.ReplaceDocument(goal.Id, System.Text.Encoding.UTF8.GetString(golden));
        Assert.Equal(oracle, JsonSerializer.SerializeToUtf8Bytes(
            await fixture.Repository.LoadGoalAsync(new(goal.Id)), oldOptions));
        Assert.Equal(golden, System.Text.Encoding.UTF8.GetBytes(fixture.RawGoal(goal.Id)));
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(oracle, JsonSerializer.SerializeToUtf8Bytes(
            await fixture.Repository.LoadGoalAsync(new(goal.Id)), oldOptions));
    }

    private void ReportDifference(string label, byte[] expected, byte[] actual)
    {
        var offset = 0;
        while (offset < Math.Min(expected.Length, actual.Length) && expected[offset] == actual[offset]) offset++;
        output.WriteLine($"{label}: goldenBytes={expected.Length}; actualBytes={actual.Length}; firstDifference={offset}");
    }

}
