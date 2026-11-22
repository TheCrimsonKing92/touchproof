using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the workspace is local and the output capture is scoped to this loop.
public sealed class CostGoalOutcomeTickOrderTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Fact]
    public void CompletedTick_EmitsHeartbeatAfterCostOutcomeSweep()
    {
        var root = CreateTempDirectory("activation-outcome-sweep");
        try
        {
            var registry = new OrchestratorProjectRegistry(Path.Combine(root, "registry"), root);
            var workspace = OrchestratorWorkspace.ForDirectory(root, root, null, registry);
            var store = new CostGoalOutcomeStore(workspace.OrchestratorDirectory);
            var now = new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
            store.RecordFiling(GoalId.New(), new(now.AddDays(-1), null,
                [new(new("selection-milliseconds", "decrease", "10%", 2), 100, 2, null)]));
            var loop = new ConductorBatchLoop(workspace: workspace, utcNow: () => now, loadTrxCoherenceVerdicts: _ => { });
            var sweep = new CostGoalOutcomeSweep(workspace.OrchestratorDirectory, store,
                () => throw new InvalidOperationException("No landing in this fixture."),
                _ =>
                {
                    Console.WriteLine("COST_OUTCOME_SWEEP_RAN");
                    return null;
                }, () => [], (_, _) => false, _ => { });
            CostGoalOutcomeSweep.UseForTests(loop, sweep);
            var (kernel, _) = SimpleGoal();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

            var lines = AsyncLocalConsoleRouter.Capture(() =>
                loop.Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                    maxIterations: 1, watchInterval: TimeSpan.FromSeconds(1),
                    sleepFunc: _ => false, emitActivationHeartbeat: true))
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            var completion = Assert.Single(lines, line => line.StartsWith("TICK_END tick=1 activation=true", StringComparison.Ordinal));
            var observation = Assert.Single(lines, line => line == "COST_OUTCOME_SWEEP_RAN");
            Assert.True(Array.IndexOf(lines, observation) < Array.IndexOf(lines, completion));
        }
        finally { TryDeleteDirectory(root); }
    }
}
