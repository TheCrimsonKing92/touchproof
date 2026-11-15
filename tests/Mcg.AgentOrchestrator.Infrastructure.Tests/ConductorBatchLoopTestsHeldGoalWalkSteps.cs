using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsHeldGoalWalkSteps(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void RunningHoldReportsNamedSubsteps()
    {
        using var fixture = new HeldGoalWorkerFixture(1_048_576);
        var tick = ConductorBatchLoopTestsStepLedger.OneTick(fixture.Loop(), fixture.Kernel, fixture.Driver);
        Assert.Equal(1, tick.Held);
        Report(tick, ["walk-held-goal", "walk-running-refresh", "walk-hold-entry-gates",
            "walk-hold-lifecycle-reconcile", "walk-hold-lifecycle-facts", "walk-running-hold-reason"]);
    }

    [Fact]
    public void RecoveryHoldReportsNamedSubsteps()
    {
        using var fixture = new HeldGoalRecoveryFixture();
        fixture.StartBaseline();
        BatchTickSummary? tick = null;
        new ConductorBatchLoop().Run(fixture.Kernel, fixture.Driver, ConductorAutonomyPolicy.Permissive,
            NoStopPath(), maxIterations: 1, onTick: result => tick = result);
        Assert.NotNull(tick);
        Assert.Equal(1, tick.Held);
        Report(tick, ["walk-held-goal", "walk-hold-entry-gates", "walk-recovery-facts",
            "walk-candidate-revision", "walk-recovery-evidence-attempt"]);
    }

    private void Report(BatchTickSummary tick, string[] names)
    {
        var line = ConductorBatchLoopTestsStepLedger.Phase(tick, "per-goal-walk");
        output.WriteLine(line); // Acceptance supplies these measured exclusive CPU columns to Reviewer.
        foreach (var name in names.Append("walk-goal-overhead"))
            Assert.Matches(Regex.Escape(name) + @":\d+:\d+:[1-9]\d*(?:,| )", line);
    }
}
