using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: flagged and inline forms each own their database.
public sealed class GoalTaskHistoryLatestInTableWrittenBypassReaderTests
{
    [Theory]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Verified)]
    [InlineData(GoalStatus.Completed)]
    public async Task WrittenFlagsAndInlineForm_ReturnSameLatestEvidence(GoalStatus status)
    {
        using var flagged = new GoalTaskHistoryTestFixture();
        using var inline = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot() with { Status = status };
        await PreparePair(flagged, inline, goal);
        var flaggedAttention = Assert.Single((await flagged.Repository.ReadAttentionProjectionAsync())!.ProviderEvidence);
        var inlineAttention = Assert.Single((await inline.Repository.ReadAttentionProjectionAsync())!.ProviderEvidence);
        using var flaggedConnection = flagged.Open();
        using var inlineConnection = inline.Open();
        var flaggedProvider = Assert.Single(await ProviderHoldSeedEvidenceReader.ReadAsync(flaggedConnection, [new(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default));
        var inlineProvider = Assert.Single(await ProviderHoldSeedEvidenceReader.ReadAsync(inlineConnection, [new(goal.Id)],
            GoalTaskHistoryTestFixture.Options, default));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(inlineAttention), GoalTaskHistoryTestFixture.Json(flaggedAttention));
        Assert.Equal(GoalTaskHistoryTestFixture.Json(inlineProvider), GoalTaskHistoryTestFixture.Json(flaggedProvider));
        foreach (var evidence in new[] { flaggedAttention, flaggedProvider })
        {
            Assert.Equal(goal.Tasks.Count, evidence.Tasks.Count);
            foreach (var task in evidence.Tasks)
            {
                var source = goal.Tasks.Single(t => t.Id == task.TaskId);
                Assert.Equal(GoalTaskHistoryTestFixture.Json(source.LastVerification), GoalTaskHistoryTestFixture.Json(task.LastVerification));
                Assert.Equal(source.LastDispatch!.Command, task.LastDispatch!.Command);
                Assert.Equal(source.LastDispatch.DispatchedAt, task.LastDispatch.DispatchedAt);
            }
        }
        var flaggedMetadata = Assert.Single(await flagged.Repository.ListConductLoopGoalMetadataAsync());
        var inlineMetadata = Assert.Single(await inline.Repository.ListConductLoopGoalMetadataAsync());
        Assert.Equal(goal.Tasks[^1].LastDispatch!.ResultCommit, flaggedMetadata.ResultCommit);
        Assert.Equal(inlineMetadata.ResultCommit, flaggedMetadata.ResultCommit);
        if (status == GoalStatus.Verified)
        {
            var flaggedFact = Assert.Single(await flagged.Repository.ListAcceptanceQueueFactsAsync());
            var inlineFact = Assert.Single(await inline.Repository.ListAcceptanceQueueFactsAsync());
            Assert.Equal(goal.Tasks.Max(task => task.LastVerification!.CompletedAt), flaggedFact.VerifiedAt);
            Assert.Equal(inlineFact, flaggedFact);
        }
        if (status == GoalStatus.Completed)
        {
            var flaggedDocument = Assert.Single(await flagged.Repository.ListCompletedGoalSimilarityDocumentsAsync());
            Assert.Equal(Assert.Single(await inline.Repository.ListCompletedGoalSimilarityDocumentsAsync()), flaggedDocument);
            Assert.Equal(goal.Id, flaggedDocument.Id);
            Assert.Equal(goal.Objective, flaggedDocument.Text);
        }
    }

    [Fact]
    public async Task ArchiveStubAndReaders_PreserveSameCommitAndEvidenceForBothForms()
    {
        using var flagged = new GoalTaskHistoryTestFixture();
        using var inline = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot();
        await PreparePair(flagged, inline, goal);
        foreach (var fixture in new[] { flagged, inline })
        {
            Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTaskHistoryTestFixture.At.AddDays(1)));
            var stub = JsonNode.Parse(fixture.RawGoal(goal.Id))!;
            Assert.True(stub["IsMetadataOnly"]!.GetValue<bool>());
            Assert.Equal(goal.Tasks[^1].LastDispatch!.ResultCommit, stub["ResultCommit"]!.GetValue<string>());
            var repository = new SqliteOrchestratorStateRepository(fixture.DatabasePath);
            Assert.Equal(goal.Tasks[^1].LastDispatch!.ResultCommit,
                Assert.Single(await repository.ListConductLoopGoalMetadataAsync()).ResultCommit);
            var attention = Assert.Single((await repository.ReadAttentionProjectionAsync())!.ProviderEvidence);
            using var connection = fixture.Open();
            var provider = Assert.Single(await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [new(goal.Id)],
                GoalTaskHistoryTestFixture.Options, default));
            foreach (var evidence in new[] { attention, provider })
            {
                Assert.Equal(goal.Tasks.Count, evidence.Tasks.Count);
                foreach (var task in evidence.Tasks)
                {
                    var source = goal.Tasks.Single(t => t.Id == task.TaskId);
                    Assert.Equal(GoalTaskHistoryTestFixture.Json(source.LastVerification), GoalTaskHistoryTestFixture.Json(task.LastVerification));
                    Assert.Equal(source.LastDispatch!.Command, task.LastDispatch!.Command);
                }
            }
        }
        Assert.Equal(Assert.Single(await inline.Repository.ListCompletedGoalSimilarityDocumentsAsync()),
            Assert.Single(await flagged.Repository.ListCompletedGoalSimilarityDocumentsAsync()));
    }

    [Fact]
    public async Task Requeue_RemovesLatestFlagsAndValuesButRetainsBothHistories()
    {
        using var flagged = new GoalTaskHistoryTestFixture();
        using var inline = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot();
        await PreparePair(flagged, inline, goal);
        var results = new List<GoalSnapshot>();
        foreach (var fixture in new[] { flagged, inline })
        {
            var rows = fixture.HistoryRows();
            Assert.Equal(0, await OrchestratorSqliteTools.RunAsync(["requeue-task", "--db", fixture.DatabasePath,
                "--task-number", "1", "--note", "retry fixture", goal.Id]));
            var marker = JsonNode.Parse(fixture.RawGoal(goal.Id))!["Tasks"]![0]!["HistoryInTable"]!.AsObject();
            Assert.False(marker.ContainsKey("LatestVerificationInTable"));
            Assert.False(marker.ContainsKey("LatestDispatchInTable"));
            Assert.Equal(goal.Tasks[0].VerificationHistory!.Count, marker["Verification"]!.GetValue<int>());
            Assert.Equal(goal.Tasks[0].DispatchHistory!.Count, marker["Dispatch"]!.GetValue<int>());
            Assert.Equal(rows, fixture.HistoryRows());
            var loaded = (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!;
            Assert.Null(loaded.Tasks[0].LastVerification);
            Assert.Null(loaded.Tasks[0].LastDispatch);
            Assert.Equal(GoalTaskHistoryTestFixture.Json(goal.Tasks[0].VerificationHistory),
                GoalTaskHistoryTestFixture.Json(loaded.Tasks[0].VerificationHistory));
            Assert.Equal(GoalTaskHistoryTestFixture.Json(goal.Tasks[0].DispatchHistory),
                GoalTaskHistoryTestFixture.Json(loaded.Tasks[0].DispatchHistory));
            results.Add(loaded);
        }
        // The command records its own current timestamp; compare the task values it clears/preserves.
        Assert.Equal(GoalTaskHistoryTestFixture.Json(results[0].Tasks), GoalTaskHistoryTestFixture.Json(results[1].Tasks));
    }

    private static async Task PreparePair(GoalTaskHistoryTestFixture flagged, GoalTaskHistoryTestFixture inline, GoalSnapshot goal)
    {
        await flagged.Repository.SaveGoalSnapshotsAsync([goal]);
        await inline.Repository.SaveGoalSnapshotsAsync([goal]);
        inline.InlineStoredLatest(goal);
        flagged.AssertLatestRepresentation(goal);
        inline.AssertLatestRepresentation(goal, false, false);
        Assert.Equal(flagged.HistoryRows(), inline.HistoryRows());
    }
}
