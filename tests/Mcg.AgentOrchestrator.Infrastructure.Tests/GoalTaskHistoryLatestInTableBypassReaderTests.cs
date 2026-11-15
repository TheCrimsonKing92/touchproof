using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: no shared state or console redirection; all utility mutations target a private database.
public sealed class GoalTaskHistoryLatestInTableBypassReaderTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SparseEvidence_ResolvesEachLatestFlagIndependently(bool verification, bool dispatch)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.LatestSnapshot();
        var goal = seed with { Tasks = seed.Tasks.Select(task => task with
        {
            LastVerification = task.VerificationHistory![0], LastDispatch = task.DispatchHistory![0]
        }).ToArray() };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.MarkStoredLatest(goal.Id, verification, dispatch, keepInline: true);
        using var connection = fixture.Open();
        var provider = Assert.Single(await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [new(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default));
        var attention = Assert.Single((await fixture.Repository.ReadAttentionProjectionAsync())!.ProviderEvidence);
        foreach (var evidence in new[] { provider, attention })
        {
            Assert.Equal(goal.Tasks.Count, evidence.Tasks.Count);
            foreach (var task in evidence.Tasks)
            {
                var source = goal.Tasks.Single(t => t.Id == task.TaskId);
                var expectedVerification = verification ? source.VerificationHistory![^1] : source.LastVerification;
                var expectedDispatch = dispatch ? source.DispatchHistory![^1] : source.LastDispatch;
                Assert.Equal(GoalTaskHistoryTestFixture.Json(expectedVerification), GoalTaskHistoryTestFixture.Json(task.LastVerification));
                Assert.Equal(expectedDispatch!.Command, task.LastDispatch!.Command);
                Assert.Equal(expectedDispatch.DispatchedAt, task.LastDispatch.DispatchedAt);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SqlProjections_UseMarkedRowsOrInlineAuthority(bool marked)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.LatestSnapshot();
        var goal = seed with { Status = GoalStatus.Verified, Tasks = seed.Tasks.Select(task => task with
        {
            LastVerification = task.VerificationHistory![0], LastDispatch = task.DispatchHistory![0]
        }).ToArray() };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.MarkStoredLatest(goal.Id, marked, marked, keepInline: !marked);
        var expectedDispatch = marked ? goal.Tasks[^1].DispatchHistory![^1] : goal.Tasks[^1].LastDispatch!;
        var expectedVerification = marked ? goal.Tasks[0].VerificationHistory![^1] : goal.Tasks[0].LastVerification!;
        var metadata = Assert.Single(await fixture.Repository.ListConductLoopGoalMetadataAsync());
        Assert.Equal(expectedDispatch.ResultCommit, metadata.ResultCommit);
        var fact = Assert.Single(await fixture.Repository.ListAcceptanceQueueFactsAsync());
        Assert.Equal(expectedVerification.CompletedAt, fact.VerifiedAt);
    }

    [Fact]
    public async Task LegacyStoreWithoutHistoryTable_SqlProjectionsKeepInlineValues()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot() with { Status = GoalStatus.Verified };
        fixture.InsertLegacy(goal);
        fixture.Execute("DROP TABLE IF EXISTS goal_task_history");
        Assert.Equal(goal.Tasks[^1].LastDispatch!.ResultCommit,
            Assert.Single(await fixture.Repository.ListConductLoopGoalMetadataAsync()).ResultCommit);
        Assert.Equal(goal.Tasks[0].LastVerification!.CompletedAt,
            Assert.Single(await fixture.Repository.ListAcceptanceQueueFactsAsync()).VerifiedAt);
    }

    [Fact]
    public async Task TerminalMetadataAndArchiveStub_PreserveMarkedCommitAndSparseEvidence()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.MarkStoredLatest(goal.Id);
        var commit = goal.Tasks[^1].LastDispatch!.ResultCommit;
        Assert.Equal(commit, Assert.Single(await fixture.Repository.ListConductLoopGoalMetadataAsync()).ResultCommit);
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTaskHistoryTestFixture.At.AddDays(1)));
        var stub = JsonNode.Parse(fixture.RawGoal(goal.Id))!;
        Assert.True(stub["IsMetadataOnly"]!.GetValue<bool>());
        Assert.Equal(commit, stub["ResultCommit"]!.GetValue<string>());
        // Use a fresh repository so terminal detail caching cannot conceal a bad stub.
        var repository = new SqliteOrchestratorStateRepository(fixture.DatabasePath);
        Assert.Equal(commit, Assert.Single(await repository.ListConductLoopGoalMetadataAsync()).ResultCommit);
        var attention = Assert.Single((await repository.ReadAttentionProjectionAsync())!.ProviderEvidence);
        using var connection = fixture.Open();
        var provider = Assert.Single(await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [new(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default));
        foreach (var evidence in new[] { attention, provider })
            foreach (var task in evidence.Tasks)
            {
                var source = goal.Tasks.Single(t => t.Id == task.TaskId);
                Assert.Equal(source.LastDispatch!.Command, task.LastDispatch!.Command);
                Assert.Equal(GoalTaskHistoryTestFixture.Json(source.LastVerification), GoalTaskHistoryTestFixture.Json(task.LastVerification));
            }
    }

    [Fact]
    public async Task Requeue_ClearsLatestReferencesAndRetainsHistoryAndOtherMarkerMembers()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.MarkStoredLatest(goal.Id);
        fixture.Execute("UPDATE goals SET snapshot_json = json_set(snapshot_json, '$.Tasks[0].HistoryInTable.Unrelated', 'retain')");
        var before = fixture.RawGoal(goal.Id);
        var rows = fixture.HistoryRows();
        string[] arguments = ["requeue-task", "--db", fixture.DatabasePath, "--task-number", "1", "--note", "retry fixture", goal.Id];
        Assert.Equal(0, await OrchestratorSqliteTools.RunAsync([.. arguments, "--dry-run"]));
        Assert.Equal(before, fixture.RawGoal(goal.Id));
        Assert.Equal(0, await OrchestratorSqliteTools.RunAsync(arguments));
        var task = JsonNode.Parse(fixture.RawGoal(goal.Id))!["Tasks"]![0]!;
        var marker = task["HistoryInTable"]!.AsObject();
        Assert.False(marker.ContainsKey("LatestVerificationInTable"));
        Assert.False(marker.ContainsKey("LatestDispatchInTable"));
        Assert.Equal(3, marker["Verification"]!.GetValue<int>());
        Assert.Equal(3, marker["Dispatch"]!.GetValue<int>());
        Assert.Equal("retain", marker["Unrelated"]!.GetValue<string>());
        Assert.Null(task["LastVerification"]);
        Assert.Null(task["LastDispatch"]);
        Assert.Equal(rows, fixture.HistoryRows());
        var loaded = (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!.Tasks[0];
        Assert.Null(loaded.LastVerification);
        Assert.Null(loaded.LastDispatch);
        Assert.Equal(3, loaded.VerificationHistory!.Count);
        Assert.Equal(3, loaded.DispatchHistory!.Count);
    }
}
