using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its database and reader instances.
public sealed class GoalTimelineInTableWrittenBypassReaderTests
{
    [Theory]
    [InlineData(GoalStatus.Verified)]
    [InlineData(GoalStatus.Completed)]
    public async Task WrittenMarker_AllBypassReadersAndArchiveResolveEvents(GoalStatus status)
    {
        using var support = new GoalTimelineTestSupport();
        var seed = GoalTimelineTestSupport.Snapshot();
        var goal = seed with { Status = status, Timeline = [seed.Timeline[0],
            seed.Timeline[1] with { Kind = ProgressKind.TaskCancelled },
            seed.Timeline[2] with { Kind = ProgressKind.GoalPolicyDecision, Message = "verified fixture",
                OccurredAt = GoalTimelineTestSupport.At.AddHours(1) }] };
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        await support.AssertMarkedAsync(goal);
        var metadata = Assert.Single(await support.Repository.ListGoalMetadataAsync(includeTerminalCreatedAt: true));
        var conduct = Assert.Single(await support.Repository.ListConductLoopGoalMetadataAsync());
        Assert.Equal(goal.Timeline[0].OccurredAt, metadata.CreatedAt);
        // Completed exercises lazy ReadTerminalMetadata; Verified exercises the SQL arm.
        Assert.Equal(goal.Timeline[0].OccurredAt, conduct.CreatedAt);
        Assert.Equal(goal.Timeline[^1].OccurredAt, conduct.TerminatedAt);
        using (var connection = support.Fixture.Open())
        {
            var epic = Assert.Single(await SqliteOrchestratorStateRepository.ReadEpicMetadataAsync(connection, [goal.Id], true));
            Assert.Equal(goal.Timeline[0].OccurredAt, epic.CreatedAt);
            var provider = Assert.Single(await ProviderHoldSeedEvidenceReader.ReadAsync(connection, [new(goal.Id)], GoalTimelineTestSupport.Options, default));
            var attention = Assert.Single((await support.Repository.ReadAttentionProjectionAsync())!.ProviderEvidence);
            foreach (var evidence in new[] { provider, attention })
            {
                var evt = Assert.Single(evidence.Timeline);
                Assert.Equal(goal.Timeline[1].Message, evt.Message);
                Assert.Equal(goal.Timeline[1].OperatorIntentCreatedAt, evt.OperatorIntentCreatedAt);
            }
        }
        var digest = Assert.Single(new OwnerDigestTimelineReader().Read(support.Fixture.DatabasePath,
            new Dictionary<string, DateTimeOffset>(), GoalTimelineTestSupport.At.AddDays(-2), GoalTimelineTestSupport.At.AddDays(2)));
        Assert.Equal(goal.Timeline.Select(evt => (evt.Message, evt.OccurredAt)), digest.Timeline.Select(evt => (evt.Message, evt.OccurredAt)));
        if (status == GoalStatus.Verified)
        {
            var queue = Assert.Single(await support.Repository.ListAcceptanceQueueFactsAsync());
            Assert.Equal(goal.Timeline[^1].OccurredAt, queue.VerifiedAt);
            Assert.Equal(goal.Timeline[0].OccurredAt, queue.CreatedAt);
            return; // Verified goals are awaiting acceptance and are not archive candidates.
        }
        else
        {
            var similarity = Assert.Single(await support.Repository.ListCompletedGoalSimilarityDocumentsAsync());
            Assert.Equal(goal.Timeline[^1].OccurredAt, similarity.UpdatedAt);
        }
        var text = support.Fixture.RawGoal(goal.Id);
        var rows = support.Rows();
        Assert.Equal(1, await support.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTimelineTestSupport.At.AddDays(1)));
        Assert.Equal(text, Assert.Single(support.Fixture.Rows("SELECT snapshot_json FROM goal_snapshot_archive")));
        var stub = JsonNode.Parse(support.Fixture.RawGoal(goal.Id))!;
        Assert.True(stub["IsMetadataOnly"]!.GetValue<bool>());
        Assert.Equal(goal.Timeline[0].OccurredAt, stub["CreatedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(goal.Timeline[^1].OccurredAt, stub["TerminatedAt"]!.GetValue<DateTimeOffset>());
        using var restore = support.Fixture.Open();
        var restored = await new GoalSnapshotArchive(GoalTimelineTestSupport.Options).HydrateAsync(restore,
            JsonSerializer.Deserialize<GoalSnapshot>(stub.ToJsonString(), GoalTimelineTestSupport.Options));
        Assert.Equal(GoalTimelineTestSupport.Json(goal.Timeline), GoalTimelineTestSupport.Json(restored!.Timeline));
        Assert.Equal(rows, support.Rows());
    }
}
