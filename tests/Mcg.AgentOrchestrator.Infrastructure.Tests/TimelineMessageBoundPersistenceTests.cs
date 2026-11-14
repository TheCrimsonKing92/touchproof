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
        var goal = kernel.CreateGoal("Persist a bounded timeline.", [task]);
        var message = "BEGIN-" + string.Concat(Enumerable.Repeat("\uD83D\uDE00", 12_000)) + "-END";
        goal.Append(new ProgressEvent(goal.Id, taskless ? null : task.Id,
            ProgressKind.TaskNote, message, new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero)));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var capped = Assert.Single(snapshot.Timeline, item => item.Kind == ProgressKind.TaskNote);
        Assert.InRange(capped.Message.Length, 1, 16_000);
        Assert.NotEqual(message, capped.Message);
        Assert.StartsWith("BEGIN-", capped.Message);
        Assert.EndsWith("-END", capped.Message);

        var root = Path.Combine(Path.GetTempPath(), "timeline-bound-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);

            await repository.SaveGoalSnapshotsAsync([snapshot]);
            var loaded = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
            Assert.Equal(capped, Assert.Single(loaded.Timeline, item => item.Kind == ProgressKind.TaskNote));

            var restoredKernel = AgentOrchestratorKernel.FromSnapshot(new([loaded], []));
            var restoredGoal = Assert.Single(restoredKernel.Goals);
            Assert.Equal(capped.Message, Assert.Single(restoredGoal.Timeline,
                item => item.Kind == ProgressKind.TaskNote).Message);
            var resaved = restoredKernel.ExportGoalSnapshot(goal.Id);
            Assert.Equal(capped, Assert.Single(resaved.Timeline, item => item.Kind == ProgressKind.TaskNote));

            await repository.SaveGoalSnapshotsAsync([resaved]);
            var reloaded = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
            Assert.Equal(capped, Assert.Single(reloaded.Timeline, item => item.Kind == ProgressKind.TaskNote));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
