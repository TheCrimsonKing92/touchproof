using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class GoalTaskBuildHistoryRoundTripTests
{
    [Fact]
    public async Task SaveAndLoadPreservesRowsAndProjectsOnlyOpenInvocations()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskBuildHistoryTestSupport.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);

        using var document = JsonDocument.Parse(fixture.RawGoal(goal.Id));
        foreach (var task in document.RootElement.GetProperty("Tasks").EnumerateArray())
        {
            var taskId = task.GetProperty("Id").GetString();
            var expected = goal.Tasks.Single(candidate => candidate.Id == taskId).WorkerBuildInvocations!;
            var inline = Assert.Single(task.GetProperty("WorkerBuildInvocations").EnumerateArray());
            Assert.Equal(GoalTaskHistoryTestFixture.Json(expected[^1]), inline.GetRawText());
            var marker = task.GetProperty("WorkerBuildInvocationsInTable").GetInt32();
            Assert.Equal(10, marker);

            var rows = fixture.Rows($"""
                SELECT sequence || '|' || entry_json FROM goal_task_build_history
                WHERE goal_id = '{goal.Id}' AND task_id = '{taskId}' AND kind = 'build'
                ORDER BY sequence
                """);
            Assert.Equal(marker, rows.Length);
            Assert.Equal(expected.Select((invocation, index) => $"{index}|{GoalTaskHistoryTestFixture.Json(invocation)}"), rows);
        }

        foreach (var loaded in await fixture.ReadLatestPathsAsync(goal))
        {
            Assert.NotEmpty(loaded.Tasks);
            foreach (var task in loaded.Tasks)
            {
                var expected = goal.Tasks.Single(candidate => candidate.Id == task.Id);
                Assert.Equal(GoalTaskHistoryTestFixture.Json(expected.WorkerBuildInvocations),
                    GoalTaskHistoryTestFixture.Json(task.WorkerBuildInvocations));
                Assert.Null(task.WorkerBuildInvocationsInTable);
            }
        }
    }

    [Fact]
    public async Task ArchivedDocumentStillHydratesReleasedInvocations()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskBuildHistoryTestSupport.Snapshot(tasks: 1, openSequence: null);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTaskHistoryTestFixture.At.AddDays(1)));

        using var stub = JsonDocument.Parse(fixture.RawGoal(goal.Id));
        Assert.True(stub.RootElement.GetProperty("IsMetadataOnly").GetBoolean());
        var loaded = (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!;
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal.Tasks[0].WorkerBuildInvocations),
            GoalTaskHistoryTestFixture.Json(loaded.Tasks[0].WorkerBuildInvocations));
    }

    [Fact]
    public async Task ReleasedTerminalUpdateBumpsVersionWithoutChangingDocument()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskBuildHistoryTestSupport.Snapshot(tasks: 1);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var raw = fixture.RawGoal(goal.Id);
        var before = Version();
        var task = goal.Tasks[0];
        var invocations = task.WorkerBuildInvocations!.ToArray();
        invocations[0] = invocations[0] with { Consumed = true, Version = 2 };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal with { Tasks = [task with { WorkerBuildInvocations = invocations }] }]);

        Assert.Equal(before + 1, Version());
        Assert.Equal(raw, fixture.RawGoal(goal.Id));
        var loaded = (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!;
        Assert.Equal(GoalTaskHistoryTestFixture.Json(invocations),
            GoalTaskHistoryTestFixture.Json(loaded.Tasks[0].WorkerBuildInvocations));

        int Version() => int.Parse(Assert.Single(fixture.Rows("SELECT CAST(version AS TEXT) FROM goals")));
    }

    [Fact]
    public async Task TerminalInvocationStaysInlineUntilCustodyIsReleased()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskBuildHistoryTestSupport.Snapshot(tasks: 1);
        var task = goal.Tasks[0];
        var invocations = task.WorkerBuildInvocations!.ToArray();
        var open = invocations[^1];
        invocations[^1] = open with
        {
            Version = 2,
            Outcome = WorkerBuildOutcome.Cancelled,
            Observations = [..open.Observations, new(WorkerBuildPhase.Completed, open.DispatchAt)]
        };
        await SaveAsync();
        using var before = JsonDocument.Parse(fixture.RawGoal(goal.Id));
        var inline = Assert.Single(before.RootElement.GetProperty("Tasks")[0]
            .GetProperty("WorkerBuildInvocations").EnumerateArray());
        Assert.Equal(GoalTaskHistoryTestFixture.Json(invocations[^1]), inline.GetRawText());

        invocations[^1] = invocations[^1] with { CustodyReleased = true, Version = 3 };
        await SaveAsync();
        using var after = JsonDocument.Parse(fixture.RawGoal(goal.Id));
        Assert.Empty(after.RootElement.GetProperty("Tasks")[0].GetProperty("WorkerBuildInvocations").EnumerateArray());
        Assert.Equal("10", Assert.Single(fixture.Rows("SELECT CAST(COUNT(*) AS TEXT) FROM goal_task_build_history")));
        var loaded = (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!;
        Assert.Equal(GoalTaskHistoryTestFixture.Json(invocations),
            GoalTaskHistoryTestFixture.Json(loaded.Tasks[0].WorkerBuildInvocations));

        Task SaveAsync() => fixture.Repository.SaveGoalSnapshotsAsync(
            [goal with { Tasks = [task with { WorkerBuildInvocations = invocations }] }]);
    }
}
