using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the sweep uses a private state database and a fixture driver.
public sealed class ConductLoopReloadReuseCountersTests
{
    [Fact]
    public async Task SweepLineCountsReloadedAndReusedGoalsAcrossTicks()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var kernel = new AgentOrchestratorKernel();
        for (var index = 0; index < 3; index++)
            GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, $"Open {index}");
        await fixture.Repository.SaveAsync(kernel);
        var root = Path.GetDirectoryName(fixture.DatabasePath)!;
        var memory = new GoalReloadMemory();
        var loop = new ConductorBatchLoop(measuredSweep: _ =>
        {
            ConductorTickStepLedger.Measure("kernel-reload", () => CliPersistentStateRunner.LoadConductLoopKernel(
                fixture.Repository, executionDirectory: root, reloadMemory: memory));
            return null;
        }, processCpuTime: () => TimeSpan.Zero);
        var driver = ConductorBatchLoopTestsStepLedger.FixtureDriver();

        var initial = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
        var repeat = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);

        Assert.Contains("readiness_evaluations=0,goals_reloaded=3,goals_reused=0",
            ConductorBatchLoopTestsStepLedger.Phase(initial, "sweep"));
        Assert.Contains("readiness_evaluations=0,goals_reloaded=0,goals_reused=3",
            ConductorBatchLoopTestsStepLedger.Phase(repeat, "sweep"));
    }
}
