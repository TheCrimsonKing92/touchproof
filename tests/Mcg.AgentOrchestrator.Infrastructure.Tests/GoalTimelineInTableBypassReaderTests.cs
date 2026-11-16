using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its store; utility calls do not redirect global console state.
public sealed class GoalTimelineInTableBypassReaderTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task MetadataSqlAndSparseReaders_RespectTimelineAuthority(bool marked, bool missingTable)
    {
        using var support = new GoalTimelineTestSupport();
        var seed = GoalTimelineTestSupport.Snapshot();
        var goal = seed with { Status = GoalStatus.Verified, Timeline = [seed.Timeline[0],
            seed.Timeline[1] with { Kind = ProgressKind.TaskCancelled },
            seed.Timeline[2] with { Kind = ProgressKind.GoalPolicyDecision, Message = "verified fixture", OccurredAt = GoalTimelineTestSupport.At.AddHours(1) }] };
        await support.SeedAsync(goal, marked);
        if (missingTable) support.Fixture.Execute("DROP TABLE goal_timeline");
        else if (!marked) support.Fixture.Execute("UPDATE goal_timeline SET entry_json = '{}' ");
        var repository = support.Repository;
        var metadata = Assert.Single(await repository.ListGoalMetadataAsync(includeTerminalCreatedAt: true));
        var conduct = Assert.Single(await repository.ListConductLoopGoalMetadataAsync());
        Assert.Equal(goal.Timeline[0].OccurredAt, metadata.CreatedAt);
        Assert.Equal(goal.Timeline[0].OccurredAt, conduct.CreatedAt);
        Assert.Equal(goal.Timeline[^1].OccurredAt, conduct.TerminatedAt);
        using var connection = support.Fixture.Open();
        var epic = Assert.Single(await SqliteOrchestratorStateRepository.ReadEpicMetadataAsync(connection, [goal.Id], true));
        Assert.Equal(goal.Timeline[0].OccurredAt, epic.CreatedAt);
        var queue = Assert.Single(await repository.ListAcceptanceQueueFactsAsync());
        Assert.Equal(goal.Timeline[^1].OccurredAt, queue.VerifiedAt);
        Assert.Equal(goal.Timeline[0].OccurredAt, queue.CreatedAt);
        var provider = Assert.Single(await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [new(goal.Id)], GoalTimelineTestSupport.Options, default));
        var attention = Assert.Single((await repository.ReadAttentionProjectionAsync())!.ProviderEvidence);
        foreach (var evidence in new[] { provider, attention })
        {
            var evt = Assert.Single(evidence.Timeline);
            Assert.Equal(goal.Timeline[1].Message, evt.Message);
            Assert.Equal(goal.Timeline[1].OperatorIntentCreatedAt, evt.OperatorIntentCreatedAt);
        }
        var digest = Assert.Single(new OwnerDigestTimelineReader().Read(support.Fixture.DatabasePath,
            new Dictionary<string, DateTimeOffset>(), GoalTimelineTestSupport.At.AddDays(-2), GoalTimelineTestSupport.At.AddDays(2)));
        Assert.Equal(goal.Timeline.Select(evt => (evt.Message, evt.OccurredAt)), digest.Timeline.Select(evt => (evt.Message, evt.OccurredAt)));
    }

    [Fact]
    public async Task ArchiveAndRestore_PreserveRowsTextAndStubTimestamps()
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.SeedAsync(goal);
        var text = support.Fixture.RawGoal(goal.Id);
        var rows = support.Rows();
        var terminal = Assert.Single(await support.Repository.ListConductLoopGoalMetadataAsync());
        Assert.Equal(goal.Timeline[0].OccurredAt, terminal.CreatedAt);
        Assert.Equal(goal.Timeline[^1].OccurredAt, terminal.TerminatedAt);
        Assert.Equal(1, await support.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTimelineTestSupport.At.AddDays(1)));
        Assert.Equal(text, Assert.Single(support.Fixture.Rows("SELECT snapshot_json FROM goal_snapshot_archive")));
        var stub = JsonNode.Parse(support.Fixture.RawGoal(goal.Id))!;
        Assert.True(stub["IsMetadataOnly"]!.GetValue<bool>());
        Assert.Equal(goal.Timeline[0].OccurredAt, stub["CreatedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(goal.Timeline[^1].OccurredAt, stub["TerminatedAt"]!.GetValue<DateTimeOffset>());
        using var connection = support.Fixture.Open();
        var restored = await new GoalSnapshotArchive(GoalTimelineTestSupport.Options).HydrateAsync(connection,
            JsonSerializer.Deserialize<GoalSnapshot>(stub.ToJsonString(), GoalTimelineTestSupport.Options));
        Assert.Equal(GoalTimelineTestSupport.Json(goal.Timeline), GoalTimelineTestSupport.Json(restored!.Timeline));
        Assert.Equal(rows, support.Rows());
    }

    [Fact]
    public async Task Archive_DefectiveRowsRemainHot()
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await support.SeedAsync(goal);
        support.Fixture.Execute("UPDATE goal_timeline SET sequence = 3 WHERE sequence = 2");
        var text = support.Fixture.RawGoal(goal.Id);
        Assert.Equal(0, await support.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTimelineTestSupport.At.AddDays(1)));
        Assert.Equal(text, support.Fixture.RawGoal(goal.Id));
    }

}
