using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalLeanTickReloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TickReloadKeepsLatestAndCountsWithPastRowsAvailableInStoredOrder(bool archived)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot(entries: 4, taskCount: 3);
        seed = seed with
        {
            Timeline = Enumerable.Range(0, 5).Select(index => new ProgressEventSnapshot(
                seed.Id, null, ProgressKind.TaskNote, $"Event {index}",
                GoalTaskHistoryTestFixture.At.AddMinutes(index / 2))).ToArray()
        };
        await fixture.Repository.SaveGoalSnapshotsAsync([seed]);
        fixture.AssertLatestRepresentation(seed);
        using var document = JsonDocument.Parse(fixture.RawGoal(seed.Id));
        if (archived)
        {
            Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(
                GoalTaskHistoryTestFixture.At.AddDays(1), compressSnapshots: true));
        }
        var id = new GoalId(seed.Id);
        var memory = new GoalReloadMemory();
        var reload = await fixture.Repository.LoadGoalsReusingUnchangedAsync([id], memory);

        var snapshot = memory.Entries[seed.Id].Snapshot;
        Assert.Equal((1, 0), (reload.Reloaded, reload.Reused));
        Assert.False(snapshot.IsMetadataOnly);
        Assert.Empty(snapshot.Timeline);
        Assert.Equal(seed.Timeline.Count, snapshot.DeferredTimeline!.Count);
        Assert.Equal(seed.Tasks.Count, snapshot.Tasks.Count);
        Assert.Equal(
            seed.Tasks.Select(task => task.LastVerification),
            snapshot.Tasks.Select(task => task.LastVerification));
        Assert.Equal(
            seed.Tasks.Select(task => task.LastDispatch),
            snapshot.Tasks.Select(task => task.LastDispatch));
        Assert.All(snapshot.Tasks, task =>
        {
            Assert.Empty(task.VerificationHistory!);
            Assert.Empty(task.DispatchHistory!);
            Assert.Equal(4, task.DeferredHistory!.VerificationCount);
            Assert.Equal(4, task.DeferredHistory.DispatchCount);
        });

        var goal = reload.Kernel.GetGoal(id);
        var appended = new ProgressEvent(id, null, ProgressKind.TaskNote, "Appended in process",
            GoalTaskHistoryTestFixture.At.AddHours(1));
        goal.Append(appended);
        var checkpoint = Assert.Single(OrchestratorSnapshot.Capture(reload.Kernel, includeDeferredRows: false).Goals);
        Assert.Equal(appended.Message, Assert.Single(checkpoint.Timeline).Message);
        Assert.All(checkpoint.Tasks, task => Assert.Empty(task.VerificationHistory!));

        using var connection = fixture.Open();
        var timeline = await new GoalTimelineStore(GoalTaskHistoryTestFixture.Options)
            .ReadAsync(connection, seed.Id, document.RootElement);
        Assert.Equal(seed.Timeline.Select(GoalTaskHistoryTestFixture.Json), timeline.Select(evt => evt.GetRawText()));
        Assert.Equal(seed.Timeline.Select(evt => evt.Message).Append(appended.Message),
            goal.Timeline.Select(evt => evt.Message));

        var taskDocuments = document.RootElement.GetProperty("Tasks").EnumerateArray().ToArray();
        for (var index = 0; index < seed.Tasks.Count; index++)
        {
            var expected = seed.Tasks[index];
            var task = goal.Tasks[index];
            var history = await fixture.Store.ReadAsync(connection, seed.Id, taskDocuments[index]);
            Assert.Equal(expected.VerificationHistory!.Select(GoalTaskHistoryTestFixture.Json),
                history.Verification.Entries.Select(entry => entry.GetRawText()));
            Assert.Equal(expected.DispatchHistory!.Select(GoalTaskHistoryTestFixture.Json),
                history.Dispatch.Entries.Select(entry => entry.GetRawText()));
            Assert.Equal(expected.VerificationHistory!.Select(entry => entry.StandardOutput),
                task.VerificationHistory.Select(entry => entry.StandardOutput));
            Assert.Equal(expected.DispatchHistory!.Select(entry => entry.DispatchedAt),
                task.DispatchHistory.Select(entry => entry.DispatchedAt));
        }
    }

    [Fact]
    public async Task TickTimelineCombinesNewerStoredRowsAndUncommittedAppendsWithoutDuplicates()
    {
        using var support = new GoalTimelineTestSupport();
        var seed = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([seed]);
        var id = new GoalId(seed.Id);
        var memory = new GoalReloadMemory();
        var reload = await support.Repository.LoadGoalsReusingUnchangedAsync([id], memory);
        var goal = reload.Kernel.GetGoal(id);
        var at = GoalTimelineTestSupport.At.AddHours(1);
        var eventSavedByAnotherWriter = new ProgressEventSnapshot(seed.Id, null, ProgressKind.TaskNote, "saved once", at);
        goal.Append(new(id, null, ProgressKind.TaskNote, eventSavedByAnotherWriter.Message, at));
        goal.Append(new(id, null, ProgressKind.TaskNote, "still staged", at.AddMinutes(1)));

        await support.Repository.SaveGoalSnapshotsAsync([seed with { Timeline = [.. seed.Timeline, eventSavedByAnotherWriter] }]);

        Assert.Equal(seed.Timeline.Select(evt => evt.Message).Concat(["saved once", "still staged"]),
            goal.Timeline.Select(evt => evt.Message));
    }

    [Fact]
    public async Task TickReloadDefersDecodingOlderRowsUntilAHistoryRead()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.LatestSnapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([seed]);
        fixture.Execute("UPDATE goal_task_history SET entry_json = '[]' WHERE kind = 'verification' AND sequence = 0");
        var id = new GoalId(seed.Id);
        var memory = new GoalReloadMemory();
        var reload = await fixture.Repository.LoadGoalsReusingUnchangedAsync([id], memory);

        var task = reload.Kernel.GetGoal(id).Tasks[0];
        Assert.Equal(seed.Tasks[0].LastVerification!.StandardOutput, task.LastVerification!.StandardOutput);
        Assert.Throws<InvalidDataException>(() => _ = task.VerificationHistory);
    }

    [Fact]
    public async Task IndependentLoadsAndBothTransactionBaselinesFullyHydrate()
    {
        using var support = new GoalTimelineTestSupport();
        var seed = GoalTimelineTestSupport.Snapshot();
        await support.Repository.SaveGoalSnapshotsAsync([seed]);
        var id = new GoalId(seed.Id);
        var memory = new GoalReloadMemory();
        await support.Repository.LoadGoalsReusingUnchangedAsync([id], memory);
        Assert.Empty(memory.Entries[seed.Id].Snapshot.Timeline);

        foreach (var snapshot in await support.Fixture.ReadLatestPathsAsync(seed))
        {
            Assert.Null(snapshot.TickRowVersion);
            Assert.Equal(GoalTimelineTestSupport.Json(seed.Timeline), GoalTimelineTestSupport.Json(snapshot.Timeline));
            foreach (var task in snapshot.Tasks)
            {
                var expected = Assert.Single(seed.Tasks, candidate => candidate.Id == task.Id);
                Assert.Equal(GoalTimelineTestSupport.Json(expected.VerificationHistory),
                    GoalTimelineTestSupport.Json(task.VerificationHistory));
                Assert.Equal(GoalTimelineTestSupport.Json(expected.DispatchHistory),
                    GoalTimelineTestSupport.Json(task.DispatchHistory));
            }
        }
    }
}
