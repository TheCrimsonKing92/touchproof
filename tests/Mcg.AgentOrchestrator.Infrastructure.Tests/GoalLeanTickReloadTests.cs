using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalLeanTickReloadTests
{
    [Fact]
    public async Task MixedSnapshotOperationsReportLogicalUtf8Bytes()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot(entries: 2);
        await fixture.Repository.SaveGoalSnapshotsAsync([seed]);
        var archiveJson = " \r\n" + fixture.RawGoal(seed.Id).Replace(
            "History fixture", "café \\u2603", StringComparison.Ordinal) + "\n";
        fixture.ReplaceDocument(seed.Id, archiveJson);
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(
            GoalTaskHistoryTestFixture.At.AddDays(1), compressSnapshots: true));
        var archiveBytes = Encoding.UTF8.GetByteCount(archiveJson);
        Assert.True(archiveBytes > archiveJson.Length);

        // Archive hydration parses the same exact payload for the snapshot and the timeline marker.
        using (var exact = GoalSnapshotByteAccounting.Begin())
        using (var connection = fixture.Open())
        {
            var snapshot = await new GoalSnapshotArchive(GoalTaskHistoryTestFixture.Options)
                .HydrateAsync(connection, seed with { IsMetadataOnly = true });
            Assert.Equal("café ☃", snapshot!.Objective);
            Assert.Equal(((long?)2 * archiveBytes, (long?)0), exact.Seal());
        }

        using (var streamed = GoalSnapshotByteAccounting.Begin())
        {
            var dispatches = await fixture.Repository.ReadArchivedDispatchUsageAsync((_, _) => true);
            Assert.NotEmpty(dispatches);
            Assert.Equal(((long?)archiveBytes, (long?)0), streamed.Seal());
        }

        using (var connection = fixture.Open())
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE goal_snapshot_archive SET snapshot_json = $json WHERE goal_id = $id";
            update.Parameters.AddWithValue("$json", archiveJson);
            update.Parameters.AddWithValue("$id", seed.Id);
            Assert.Equal(1, update.ExecuteNonQuery());
            using var legacyText = GoalSnapshotByteAccounting.Begin();
            var snapshot = await new GoalSnapshotArchive(GoalTaskHistoryTestFixture.Options)
                .HydrateAsync(connection, seed with { IsMetadataOnly = true });
            Assert.Equal("café ☃", snapshot!.Objective);
            Assert.Equal(((long?)null, (long?)0), legacyText.Seal());
        }

        var hotId = new GoalId("bbbbbbbb000000000000000000000000");
        var hot = seed with
        {
            Id = hotId.Value,
            Timeline = seed.Timeline.Select(entry => entry with { GoalId = hotId.Value }).ToArray()
        };
        await fixture.Repository.SaveGoalSnapshotsAsync([hot]);
        var memory = new GoalReloadMemory();
        var loaded = await fixture.Repository.LoadGoalsReusingUnchangedAsync([hotId], memory);
        Assert.Equal(1, loaded.Reloaded);

        using (var cached = GoalSnapshotByteAccounting.Begin())
        {
            var reused = await fixture.Repository.LoadGoalsReusingUnchangedAsync([hotId], memory);
            Assert.Equal((0, 1), (reused.Reloaded, reused.Reused));
            Assert.Equal(((long?)0, (long?)0), cached.Seal());
        }

        using (var deferred = GoalSnapshotByteAccounting.Begin())
        {
            Assert.Equal("Created", Assert.Single(loaded.Kernel.GetGoal(hotId).Timeline).Message);
            Assert.Equal(((long?)null, (long?)0), deferred.Seal());
        }

        var changed = hot with { Objective = "Change café" };
        using (var mixed = GoalSnapshotByteAccounting.Begin())
        {
            var reused = await fixture.Repository.LoadGoalsReusingUnchangedAsync([hotId], memory);
            Assert.Equal((0, 1), (reused.Reloaded, reused.Reused));
            await fixture.Repository.SaveGoalSnapshotsAsync([changed]);
            await fixture.Repository.LoadGoalsAsync([hotId]);
            Assert.Equal(((long?)null, (long?)Encoding.UTF8.GetByteCount(fixture.RawGoal(hot.Id))), mixed.Seal());
        }

        using (var unchanged = GoalSnapshotByteAccounting.Begin())
        {
            await fixture.Repository.SaveGoalSnapshotsAsync([changed]);
            Assert.Equal(((long?)0, (long?)null), unchanged.Seal());
        }

        fixture.Execute("""
            CREATE TRIGGER fixture_reject_goal_update BEFORE UPDATE ON goals
            BEGIN SELECT RAISE(ABORT, 'fixture rejected goal upsert'); END
            """);
        using (var failed = GoalSnapshotByteAccounting.Begin())
        {
            var error = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                fixture.Repository.SaveGoalSnapshotsAsync([changed with { Objective = "Rejected" }]));
            Assert.Contains("fixture rejected goal upsert", error.Message);
            Assert.Equal(((long?)0, (long?)null), failed.Seal());
        }

        using (var beforeSerialization = GoalSnapshotByteAccounting.Begin())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Repository.SaveGoalSnapshotsAsync([changed with { IsMetadataOnly = true }]));
            Assert.Equal(((long?)0, (long?)0), beforeSerialization.Seal());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TickReloadKeepsLatestAndCountsWithPastRowsAvailableInStoredOrder(bool archived)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot(entries: 4, taskCount: 3);
        seed = seed with
        {
            Tasks = seed.Tasks.Select(task => task with { WorkerBuildInvocations = [] }).ToArray(),
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
