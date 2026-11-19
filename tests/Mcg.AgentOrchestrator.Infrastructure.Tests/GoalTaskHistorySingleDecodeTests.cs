using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its database and read observer.
public sealed class GoalTaskHistorySingleDecodeTests
{
    [Fact]
    public async Task HotReadMatchesTwoDecodeReaderWithoutParsingMarkersAgain()
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await SeedMarkedGoalAsync(support, goal);
        var id = new GoalId(goal.Id);
        var reads = new List<GoalSnapshotReadObservation>();
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(support.Fixture.DatabasePath, reads.Add);
        var text = support.Fixture.RawGoal(goal.Id);
        var inlineOnly = JsonSerializer.Deserialize<GoalSnapshot>(text, GoalTimelineTestSupport.Options)!;
        Assert.Null(inlineOnly.Timeline);
        Assert.All(inlineOnly.Tasks, task =>
        {
            Assert.Null(task.VerificationHistory);
            Assert.Null(task.DispatchHistory);
        });

        using var connection = support.Fixture.Open();
        var legacy = await support.Fixture.Store.HydrateAsync(connection, inlineOnly, text, readObserver: reads.Add);
        var prior = Assert.Single(reads, read => read.Operation == "history-marker-decode");
        Assert.Equal(goal.Id, prior.GoalId);
        Assert.Equal(text.Length, prior.MaterializedCharacters);
        Assert.Equal(GoalTimelineTestSupport.Json(goal), GoalTimelineTestSupport.Json(legacy));

        reads.Clear();
        var current = (await repository.LoadGoalsAsync([id])).ExportGoalSnapshot(id);
        Assert.Equal(GoalTimelineTestSupport.Json(legacy), GoalTimelineTestSupport.Json(current));
        Assert.Equal(goal.Id, Assert.Single(reads, read => read.Operation == "hot-attempt").GoalId);
        Assert.Equal(goal.Id, Assert.Single(reads, read => read.Operation == "hot-deserialized").GoalId);
        Assert.DoesNotContain(reads, read => read.Operation == "history-marker-decode");
    }

    [Fact]
    public async Task ArchivedStubStillReadsArchiveMarkers()
    {
        using var support = new GoalTimelineTestSupport();
        var goal = GoalTimelineTestSupport.Snapshot();
        await SeedMarkedGoalAsync(support, goal);
        Assert.Equal(1, await support.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTimelineTestSupport.At.AddDays(1)));
        Assert.Contains("\"IsMetadataOnly\":true", support.Fixture.RawGoal(goal.Id));
        var reads = new List<GoalSnapshotReadObservation>();
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(support.Fixture.DatabasePath, reads.Add);
        var id = new GoalId(goal.Id);

        var loaded = (await repository.LoadGoalsAsync([id])).ExportGoalSnapshot(id);

        Assert.Equal(GoalTimelineTestSupport.Json(goal), GoalTimelineTestSupport.Json(loaded));
        Assert.Equal(goal.Id, Assert.Single(reads, read => read.Operation == "hot-deserialized").GoalId);
        Assert.Equal(goal.Id, Assert.Single(reads, read => read.Operation == "archive-deserialized").GoalId);
        Assert.Equal(goal.Id, Assert.Single(reads, read => read.Operation == "history-marker-decode").GoalId);
    }

    private static async Task SeedMarkedGoalAsync(GoalTimelineTestSupport support, GoalSnapshot goal)
    {
        await support.Repository.SaveGoalSnapshotsAsync([goal]);
        support.Fixture.MarkStoredHistory(goal.Id);
    }
}
