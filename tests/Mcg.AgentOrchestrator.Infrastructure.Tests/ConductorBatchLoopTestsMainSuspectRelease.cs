using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorBatchLoopTestsMainSuspectRelease
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task DrainObserved_DoesNotStartMainSuspectReleaseProbe()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        scenario.Run();
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await scenario.Circuit.ReadAsync()).Health);

        var tip = new AcceptanceEngineMainSuspectReleaseTests.TipReader();
        var probe = new AcceptanceEngineMainSuspectReleaseTests.ProbeStub(MainSuspectReleaseProbeOutcome.Passed);
        var release = new AcceptanceEngineMainSuspectRelease(scenario.Events, scenario.Circuit, tip, probe, _ => { });
        var loop = new ConductorBatchLoop(acceptanceEngineCircuit: scenario.Circuit,
            canaryDrainClock: new ManualStewardTimeProvider()).WithMainSuspectRelease(release);
        var stopPath = Path.Combine(scenario.Repo, "stop-does-not-exist");
        ConductorDrainSignal.Write(ConductorDrainSignal.PathFor(stopPath), 1800);

        var summary = loop.Run(scenario.Kernel, scenario.Driver, ConductorAutonomyPolicy.Permissive, stopPath,
            sleepFunc: _ => throw new InvalidOperationException("An empty drain must finish before sleeping."),
            persistGoalTick: (_, _) => { });

        Assert.Equal(ConductorDrainSignal.DrainedReason, summary.StopReason);
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public async Task TickContinuesWhileProbeIsInFlightAndDrainsBeforeReturning()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        scenario.Run();
        var tip = new AcceptanceEngineMainSuspectReleaseTests.TipReader();
        var probe = new AcceptanceEngineMainSuspectReleaseTests.ProbeStub(MainSuspectReleaseProbeOutcome.Passed);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MainSuspectReleaseProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();
        probe.Handler = (_, _, _) => { entered.SetResult(); return finish.Task; };
        var release = new AcceptanceEngineMainSuspectRelease(scenario.Events, scenario.Circuit, tip, probe, lines.Add);
        var loop = new ConductorBatchLoop(acceptanceEngineCircuit: scenario.Circuit).WithMainSuspectRelease(release);
        var run = Task.Run(() => loop.Run(scenario.Kernel, scenario.Driver, ConductorAutonomyPolicy.Permissive,
            Path.Combine(scenario.Repo, "stop-does-not-exist"), maxIterations: 1,
            onTick: _ => tick.SetResult(), persistGoalTick: (_, _) => { }));
        try
        {
            await entered.Task;
            await tick.Task;
            Assert.False(run.IsCompleted);
            Assert.Single(probe.Calls);
            Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await scenario.Circuit.ReadAsync()).Health);
        }
        finally
        {
            finish.SetResult(new(MainSuspectReleaseProbeOutcome.Passed, "loop-probe-receipt"));
            await run;
        }
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);
        Assert.Contains("result=released", Assert.Single(lines));
    }
}
