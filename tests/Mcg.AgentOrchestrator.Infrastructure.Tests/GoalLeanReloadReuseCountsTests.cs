using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the sweep uses a private state database and a fixture driver.
public sealed class GoalLeanReloadReuseCountsTests
{
    [Fact]
    public async Task SweepCountsOneLeanReloadAndTwoReusesAfterOneGoalChanges()
    {
        using var support = new GoalTimelineTestSupport();
        var seed = GoalTimelineTestSupport.Snapshot() with { Status = GoalStatus.Active };
        var kernel = new AgentOrchestratorKernel();
        for (var index = 0; index < 2; index++)
            GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, $"Open {index}");
        await support.Repository.SaveAsync(kernel);
        await support.Repository.SaveGoalSnapshotsAsync([seed]);

        var root = Path.GetDirectoryName(support.Fixture.DatabasePath)!;
        var memory = new GoalReloadMemory();
        var loop = new ConductorBatchLoop(measuredSweep: _ =>
        {
            ConductorTickStepLedger.Measure("kernel-reload", () => CliPersistentStateRunner.LoadConductLoopKernel(
                support.Repository, executionDirectory: root, reloadMemory: memory));
            return null;
        }, processCpuTime: () => TimeSpan.Zero);
        var driver = ConductorBatchLoopTestsStepLedger.FixtureDriver();

        var initial = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
        Assert.Contains("goals_reloaded=3,goals_reused=0", ConductorBatchLoopTestsStepLedger.Phase(initial, "sweep"));
        var before = memory.Entries;
        var appended = new ProgressEventSnapshot(seed.Id, null, ProgressKind.TaskNote,
            "Changed between ticks", GoalTimelineTestSupport.At.AddHours(1));
        await support.Repository.SaveGoalSnapshotsAsync([seed with { Timeline = [.. seed.Timeline, appended] }]);

        var mixed = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
        Assert.Contains("goals_reloaded=1,goals_reused=2", ConductorBatchLoopTestsStepLedger.Phase(mixed, "sweep"));
        Assert.Equal(before[seed.Id].Version + 1, memory.Entries[seed.Id].Version);
        foreach (var goal in kernel.Goals)
            Assert.Same(before[goal.Id.Value].Snapshot, memory.Entries[goal.Id.Value].Snapshot);

        var reloaded = memory.Entries[seed.Id].Snapshot;
        Assert.Empty(reloaded.Timeline);
        Assert.Equal(seed.Timeline.Count + 1, reloaded.DeferredTimeline!.Count);
        Assert.All(reloaded.Tasks, task =>
        {
            Assert.Empty(task.VerificationHistory!);
            Assert.Empty(task.DispatchHistory!);
            var original = Assert.Single(seed.Tasks, candidate => candidate.Id == task.Id);
            Assert.Equal(original.LastVerification, task.LastVerification);
            Assert.Equal(original.LastDispatch, task.LastDispatch);
        });
    }
}
