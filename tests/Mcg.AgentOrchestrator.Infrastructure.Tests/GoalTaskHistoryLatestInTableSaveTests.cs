using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each save path owns its database.
public sealed class GoalTaskHistoryLatestInTableSaveTests
{
    [Theory]
    [InlineData("snapshots")]
    [InlineData("kernel")]
    [InlineData("cas")]
    [InlineData("state-cas")]
    [InlineData("merge")]
    public async Task LoadMarkedThenSave_WritesInlineLatestAndOnlyCountMarkers(string path)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        AssertInline(fixture, goal);
        var rows = fixture.HistoryRows();
        fixture.MarkStoredLatest(goal.Id);
        var id = new GoalId(goal.Id);
        if (path == "snapshots")
            await fixture.Repository.SaveGoalSnapshotsAsync([(await fixture.Repository.LoadGoalAsync(id))!]);
        else if (path == "kernel")
            await fixture.Repository.SaveAsync(await fixture.Repository.LoadGoalsAsync([id]));
        else if (path == "cas")
            await fixture.Repository.TransactGoalAsync(id, (loaded, _) => Task.FromResult((true, loaded, true)));
        else if (path == "state-cas")
            await fixture.Repository.TransactGoalStateAsync(id, (loaded, _) => Task.FromResult((true, loaded, true)));
        else
        {
            var loaded = (await fixture.Repository.LoadGoalAsync(id))!;
            await fixture.Repository.SaveGoalSnapshotsWithMergeAsync([new(loaded, loaded with { Objective = "merge latest" })]);
        }
        AssertInline(fixture, goal);
        Assert.Equal(rows, fixture.HistoryRows());
    }

    private static void AssertInline(GoalTaskHistoryTestFixture fixture, GoalSnapshot expected)
    {
        var raw = fixture.RawGoal(expected.Id);
        Assert.DoesNotContain("LatestVerificationInTable", raw);
        Assert.DoesNotContain("LatestDispatchInTable", raw);
        using var document = JsonDocument.Parse(raw);
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
        {
            var source = expected.Tasks.Single(t => t.Id == task.GetProperty("Id").GetString());
            Assert.Equal(GoalTaskHistoryTestFixture.Json(source.VerificationHistory![^1]), task.GetProperty("LastVerification").GetRawText());
            Assert.Equal(GoalTaskHistoryTestFixture.Json(source.DispatchHistory![^1]), task.GetProperty("LastDispatch").GetRawText());
            Assert.Equal(new[] { "Dispatch", "Verification" }, task.GetProperty("HistoryInTable").EnumerateObject()
                .Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal));
        }
    }
}
