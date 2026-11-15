using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each inline case owns its database.
public sealed class GoalTaskHistoryLatestInTableInlineCaseTests
{
    [Theory]
    [InlineData("null-latest")]
    [InlineData("null-history")]
    [InlineData("empty-history")]
    [InlineData("one-field")]
    [InlineData("older-row")]
    public async Task IneligibleLatestStaysInlineWithAbsentFlagsAndRoundTrips(string scenario)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([seed]);
        fixture.AssertLatestRepresentation(seed);
        var goal = seed with { Tasks = seed.Tasks.Select(task => scenario switch
        {
            "null-latest" => task with { LastVerification = null, LastDispatch = null },
            "null-history" => task with { VerificationHistory = null, DispatchHistory = null },
            "empty-history" => task with { VerificationHistory = [], DispatchHistory = [] },
            "one-field" => task with
            {
                LastVerification = task.LastVerification! with { StandardError = "one different field" },
                LastDispatch = task.LastDispatch! with { ResultCommit = "one different field" }
            },
            _ => task with { LastVerification = task.VerificationHistory![0], LastDispatch = task.DispatchHistory![0] }
        }).ToArray() };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.AssertLatestRepresentation(goal, false, false);
        using var document = JsonDocument.Parse(fixture.RawGoal(goal.Id));
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
            Assert.Equal(new[] { "Dispatch", "Verification" }, task.GetProperty("HistoryInTable").EnumerateObject()
                .Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal),
            GoalTaskHistoryTestFixture.Json(await fixture.Repository.LoadGoalAsync(new(goal.Id))));
    }
}
