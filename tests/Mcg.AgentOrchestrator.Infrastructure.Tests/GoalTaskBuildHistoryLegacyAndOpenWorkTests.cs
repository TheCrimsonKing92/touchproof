using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalTaskBuildHistoryLegacyAndOpenWorkTests
{
    [Fact]
    public async Task UnmarkedDocumentKeepsItsInlineListUntilSaved()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskBuildHistoryTestSupport.Snapshot(tasks: 1);
        fixture.InsertLegacy(goal);
        var raw = fixture.RawGoal(goal.Id);
        using var legacy = JsonDocument.Parse(raw);
        Assert.False(legacy.RootElement.GetProperty("Tasks")[0].TryGetProperty("WorkerBuildInvocationsInTable", out _));

        var loaded = (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!;
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal.Tasks[0].WorkerBuildInvocations),
            GoalTaskHistoryTestFixture.Json(loaded.Tasks[0].WorkerBuildInvocations));
        Assert.Equal(raw, fixture.RawGoal(goal.Id));
        Assert.Empty(fixture.Rows("SELECT name FROM sqlite_schema WHERE name = 'goal_task_build_history'"));

        await fixture.Repository.SaveGoalSnapshotsAsync([loaded]);
        using var migrated = JsonDocument.Parse(fixture.RawGoal(goal.Id));
        Assert.Equal(10, migrated.RootElement.GetProperty("Tasks")[0].GetProperty("WorkerBuildInvocationsInTable").GetInt32());
        Assert.Equal("10", Assert.Single(fixture.Rows("SELECT CAST(COUNT(*) AS TEXT) FROM goal_task_build_history")));
    }

    [Fact]
    public async Task TerminalOpenWorkScanAndIndexFlagOnlyGoalWithOpenInvocation()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var open = GoalTaskBuildHistoryTestSupport.Snapshot(goalNumber: 1, tasks: 1);
        var released = GoalTaskBuildHistoryTestSupport.Snapshot(goalNumber: 2, tasks: 1, openSequence: null);
        await fixture.Repository.SaveGoalSnapshotsAsync([open, released]);

        var openId = new GoalId(open.Id);
        Assert.Equal(openId, Assert.Single(await fixture.Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync()));
        Assert.True(await fixture.Repository.EnsureTerminalOpenWorkIndexAsync());
        Assert.Equal(openId, Assert.Single(await fixture.Repository.ListTerminalGoalIdsWithNonTerminalTasksAsync()));
    }

    [Fact]
    public async Task EmptyMarkedListReloadsAsEmpty()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskBuildHistoryTestSupport.Snapshot(tasks: 1);
        goal = goal with { Tasks = [goal.Tasks[0] with { WorkerBuildInvocations = [] }] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);

        using var document = JsonDocument.Parse(fixture.RawGoal(goal.Id));
        Assert.Equal(0, document.RootElement.GetProperty("Tasks")[0].GetProperty("WorkerBuildInvocationsInTable").GetInt32());
        var loaded = (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!;
        Assert.Empty(loaded.Tasks[0].WorkerBuildInvocations!);
    }
}
