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
            var goal = GoalId.New();
            const string step = "owned-root-reap";
            store.RecordFiling(goal, new(now.AddDays(-1), null,
                [new(new($"tick-step-milliseconds:{step}", "decrease", "10%", 2), 100, 2, null)]));
            var loop = new ConductorBatchLoop(workspace: workspace, utcNow: () => now, loadTrxCoherenceVerdicts: _ => { });
            var sweep = new CostGoalOutcomeSweep(workspace.OrchestratorDirectory, store,
                () => (new BacklogStore(Path.Combine(root, "backlog.db")), new ConductEventLogWriter(Path.Combine(root, "outcome-events.log"))),
                _ => "landed", () => [("landed", now.AddMinutes(-10))], (_, _) => false, _ => { },
                readTicks: () => [new(now.AddMinutes(-2), 1, step, 10), new(now.AddMinutes(-1), 2, step, 20)]);
            CostGoalOutcomeSweep.UseForTests(loop, sweep);
            var (kernel, _) = SimpleGoal();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningCount: () => ConductorAutonomyPolicy.Conservative.MaxConcurrentPaidWorkers);

            using var heartbeat = new HeartbeatObserver(() => store.Read(goal));
            AsyncLocalConsoleRouter.CaptureTo(() =>
                loop.Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                    maxIterations: 1, watchInterval: TimeSpan.FromSeconds(1),
                    sleepFunc: _ => false, emitActivationHeartbeat: true), heartbeat);

            var record = Assert.IsType<CostGoalOutcomeRecord>(Assert.Single(heartbeat.Records));
            var verdict = Assert.Single(record.FinalVerdicts);
            Assert.Equal(CostGoalOutcomeState.Met, verdict.State);
            Assert.Equal(15, verdict.After);
            Assert.Equal(verdict.LineIndex, Assert.Single(record.EmittedLines));
        }
        finally { TryDeleteDirectory(root); }
    }

    private sealed class HeartbeatObserver(Func<CostGoalOutcomeRecord?> readRecord) : StringWriter
    {
        internal List<CostGoalOutcomeRecord?> Records { get; } = [];

        public override void WriteLine(string? value)
        {
            if (value?.StartsWith("TICK_END tick=1 activation=true ", StringComparison.Ordinal) == true)
                Records.Add(readRecord());
        }
    }
}
