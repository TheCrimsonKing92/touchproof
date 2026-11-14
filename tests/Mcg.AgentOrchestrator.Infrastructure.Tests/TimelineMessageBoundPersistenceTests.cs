using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns a temporary workspace and non-pooled SQLite connections.
public sealed class TimelineMessageBoundPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveLoad_CappedTimeline_RestoresExactTextAcrossRepeatedSaves(bool taskless)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Persist an unchanged timeline.", [task]);
        var message = "BEGIN-" + string.Concat(Enumerable.Repeat("\uD83D\uDE00", 12_000)) + "-END";
        goal.Append(new ProgressEvent(goal.Id, taskless ? null : task.Id,
            ProgressKind.TaskNote, message, new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero)));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var recorded = Assert.Single(snapshot.Timeline, item => item.Kind == ProgressKind.TaskNote);
        Assert.True(message.Length > TimelineMessageBound.CharacterLimit);
        Assert.Equal(message.Length, recorded.Message.Length);
        Assert.Equal(message, recorded.Message);

        var root = Path.Combine(Path.GetTempPath(), "timeline-bound-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);

            await repository.SaveGoalSnapshotsAsync([snapshot]);
            var loaded = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
            Assert.Equal(recorded, Assert.Single(loaded.Timeline, item => item.Kind == ProgressKind.TaskNote));

            var restoredKernel = AgentOrchestratorKernel.FromSnapshot(new([loaded], []));
            var restoredGoal = Assert.Single(restoredKernel.Goals);
            Assert.Equal(message, Assert.Single(restoredGoal.Timeline,
                item => item.Kind == ProgressKind.TaskNote).Message);
            var resaved = restoredKernel.ExportGoalSnapshot(goal.Id);
            Assert.Equal(recorded, Assert.Single(resaved.Timeline, item => item.Kind == ProgressKind.TaskNote));

            await repository.SaveGoalSnapshotsAsync([resaved]);
            var reloaded = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
            Assert.Equal(recorded, Assert.Single(reloaded.Timeline, item => item.Kind == ProgressKind.TaskNote));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveLoad_LongStartedPreTesterEntry_ParsesEverySelectionAfterReload()
    {
        var kernel = new AgentOrchestratorKernel();
        var tester = new TaskSpec(TaskId.New(), "Test.", AgentRole.Tester);
        var goal = kernel.CreateGoal("Persist every pre-Tester selection.", [tester]);
        const string candidateSha = "bbb2222";
        var selections = Enumerable.Range(0, 600)
            .Select(index => $"Infrastructure.Tests:RecordedSelection{index:D4}Tests").ToArray();
        var message = PreTesterEvidenceIndexLines.FormatMarker(
            new("started", candidateSha, "long-started-receipt", selections, [], null, []));
        Assert.True(message.Length > TimelineMessageBound.CharacterLimit);
        kernel.RecordFindingEvidenceRequest(goal.Id, tester.Id, message);

        var root = Path.Combine(Path.GetTempPath(), "timeline-bound-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveGoalSnapshotsAsync([kernel.ExportGoalSnapshot(goal.Id)]);
            var loaded = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
            var restored = Assert.Single(AgentOrchestratorKernel.FromSnapshot(new([loaded], [])).Goals);

            var entry = Assert.IsType<PreTesterEvidenceEntry>(
                PreTesterEvidenceIndexLines.Latest(restored, tester.Id, candidateSha));
            Assert.Equal("started", entry.Outcome);
            Assert.Equal(selections, entry.Selections);
            Assert.All(entry.Selections, selection => Assert.DoesNotContain("[omitted", selection, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
