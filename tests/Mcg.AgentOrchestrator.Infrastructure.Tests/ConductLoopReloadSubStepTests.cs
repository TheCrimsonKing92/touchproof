using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductLoopReloadSubStepTests
{
    [Fact]
    public async Task AllReusedReloadRecordsEveryNamedSubStep()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var kernel = new AgentOrchestratorKernel();
        const int goalCount = 3;
        for (var index = 0; index < goalCount; index++)
            GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, $"Open {index}");
        await fixture.Repository.SaveAsync(kernel);

        var root = Path.GetDirectoryName(fixture.DatabasePath)!;
        var intents = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"), Path.Combine(root, "logs"));
        var memory = new GoalReloadMemory();
        var baselines = new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal);
        var loop = new ConductorBatchLoop(measuredSweep: _ =>
        {
            ConductorTickStepLedger.Measure("kernel-reload", () => CliPersistentStateRunner.LoadConductLoopKernelForTick(
                fixture.Repository, intents, [], root, memory, baselines));
            return null;
        }, processCpuTime: () => TimeSpan.Zero);
        var driver = ConductorBatchLoopTestsStepLedger.FixtureDriver();

        var first = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
        var second = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
        var firstSweep = ConductorBatchLoopTestsStepLedger.Phase(first, "sweep");
        var secondSweep = ConductorBatchLoopTestsStepLedger.Phase(second, "sweep");

        Assert.Contains(FormattableString.Invariant($"goals_reloaded={goalCount},goals_reused=0 "), firstSweep);
        Assert.Contains(FormattableString.Invariant($"goals_reloaded=0,goals_reused={goalCount} "), secondSweep);
        var entries = Regex.Match(secondSweep, @" kernel_reload_steps=(\S+) ").Groups[1].Value
            .Split(',').Select(entry => entry.Split(':')).ToArray();
        Assert.Equal(new[]
        {
            "actionable-goal-id-query", "goal-metadata-list", "terminal-dependency-metadata-read",
            "goal-version-query", "changed-snapshot-read", "human-input-request-load", "kernel-build",
            "engineering-practice-load", "kernel-merge", "tick-baseline-capture", "remainder"
        }, entries.Select(entry => entry[0]));
        Assert.All(entries, entry =>
        {
            Assert.Equal(3, entry.Length);
            Assert.True(long.Parse(entry[2], CultureInfo.InvariantCulture) >= 1, $"No calls recorded for {entry[0]}");
        });
    }

    [Fact]
    public void SweepLineAddsReloadAttributionAndPreservesSweepSteps()
    {
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Sweep fixture");
        var cpu = TimeSpan.Zero;
        var loop = new ConductorBatchLoop(measuredSweep: _ =>
        {
            ConductorTickStepLedger.Measure("kernel-reload", () =>
            {
                ConductorTickStepLedger.MeasureKernelReload(ConductorTickStepLedger.ActionableGoalIdQuery,
                    () => { cpu += TimeSpan.FromMilliseconds(3); });
            });
            foreach (var name in new[]
            {
                "refresh-dispatches", "parked-reloads", "reconcile-remediation", "host-health", "maintenance",
                "state-log-check", "failure-clusters", "remote-git-mirror", "goal-refinement"
            })
                ConductorTickStepLedger.Measure(name, () => { cpu += TimeSpan.FromMilliseconds(3); });
            ConductorTickStepLedger.Measure("terminal-sweep-run", () =>
            {
                ConductorTickStepLedger.Measure("owned-root-reap", () => { cpu += TimeSpan.FromMilliseconds(7); });
                cpu += TimeSpan.FromMilliseconds(3);
            });
            cpu += TimeSpan.FromMilliseconds(5);
            return null;
        }, processCpuTime: () => cpu);

        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, ConductorBatchLoopTestsStepLedger.FixtureDriver());
        var sweep = ConductorBatchLoopTestsStepLedger.Phase(tick, "sweep");

        Assert.Contains(" sweep_steps=kernel-reload:3:1,refresh-dispatches:3:1,parked-reloads:3:1,terminal-sweep-run:3:1,owned-root-reap:7:1,reconcile-remediation:3:1,host-health:3:1,maintenance:3:1,state-log-check:3:1,failure-clusters:3:1,remote-git-mirror:3:1,goal-refinement:3:1,program-health:0:0 load_counters=", sweep);
        Assert.Matches(@" load_counters=\S+ kernel_reload_steps=actionable-goal-id-query:\d+:1,\S+,remainder:\d+:1 cpu_ms=45$", sweep);
    }
}
