using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: injected worker/probe seams; no process is started and no ambient environment changes.
public sealed class EnvironmentHoldDispatchTests
{
    [Fact]
    public void FailingProbe_HoldsWithoutWorkerOrOwnerRound_ThenSelfClearsAndStarts()
    {
        var (kernel, goal) = SimpleGoal();
        var runner = new EnvironmentContractTestFixture.Runner();
        var events = new List<(string Kind, string? Goal, string Detail)>();
        var starts = 0;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); });
        driver.EnvironmentGate = new(kernel, "project", Path.GetTempPath(), () => EnvironmentContractTestFixture.Contract,
            runner, _ => EnvironmentContractTestFixture.Environment(), (kind, id, detail) => events.Add((kind, id, detail)));
        var policy = ConductorAutonomyPolicy.Conservative;

        var beforeHold = GoalKernelChange.Capture(goal);
        var result = driver.AdvanceOnce(goal, policy);
        Assert.True(GoalKernelChange.Changed(beforeHold, kernel, goal.Id));
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(ConductorHoldOwner.Environment, held.Owner);
        Assert.Equal("environment-contract", held.Decision!.Stage);
        Assert.Equal(0, starts);
        var hold = Assert.IsType<EnvironmentHold>(EnvironmentHold.TryRead(goal));
        Assert.Equal("cargo nextest", hold.Tool);
        Assert.Equal("cargo nextest --version", hold.Command);
        Assert.Equal(7, hold.ExitCode);
        Assert.Equal("missing nextest output tail", hold.OutputTail);
        Assert.Equal(OperatorDispositionState.Blocked, new GoalOperatorDispositionSurface().Evaluate(goal, 0, false).State);
        using var attention = JsonDocument.Parse(AttentionShowAgentReadView.Render(goal.Id.Value, [], [], _ => null, goals: [goal]));
        var attentionHold = Assert.Single(attention.RootElement.GetProperty("payload").GetProperty("environmentHolds").EnumerateArray());
        Assert.Equal("operator", attentionHold.GetProperty("owner").GetString());
        Assert.Equal("cargo nextest", attentionHold.GetProperty("hold").GetProperty("tool").GetString());
        Assert.Equal("cargo nextest --version", attentionHold.GetProperty("hold").GetProperty("command").GetString());
        var changes = new HashSet<GoalId>();
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held, DateTimeOffset.UtcNow, TimeSpan.Zero, changes, []);
        Assert.Contains(goal.Id, changes);
        driver.AdvanceOnce(goal, policy);
        Assert.Equal(hold, EnvironmentHold.TryRead(goal));
        Assert.Single(events, item => item.Kind == "environment-hold");
        Assert.Equal(1, goal.Tasks.Count);
        Assert.Equal(0, starts);

        runner.Pass = true;
        var beforeClear = GoalKernelChange.Capture(goal);
        var resumed = driver.AdvanceOnce(goal, policy);
        Assert.True(GoalKernelChange.Changed(beforeClear, kernel, goal.Id));
        Assert.IsType<ConductorAdvanceOutcome.Executed>(resumed.Outcome);
        Assert.Null(goal.CurrentHold);
        Assert.Equal(1, starts);
        var cleared = Assert.Single(events, item => item.Kind == "environment-hold-cleared");
        using var detail = JsonDocument.Parse(cleared.Detail);
        Assert.Equal("project", detail.RootElement.GetProperty("project").GetString());
        Assert.Equal("cargo nextest", detail.RootElement.GetProperty("tool").GetString());
        Assert.Equal("pass", detail.RootElement.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(detail.RootElement.GetProperty("fingerprint").GetString()));
    }

    [Fact]
    public void Startup_EmitsOneStructuredPassPerTool()
    {
        var (kernel, goal) = SimpleGoal();
        var runner = new EnvironmentContractTestFixture.Runner { Pass = true };
        var events = new List<string>();
        var gate = new EnvironmentContractGate(kernel, "project", Path.GetTempPath(), () => EnvironmentContractTestFixture.Contract,
            runner, _ => EnvironmentContractTestFixture.Environment(), (kind, _, _) => events.Add(kind));
        gate.ProbeAtStart([goal]);
        Assert.Equal(["environment-probe-pass"], events);
        Assert.Null(gate.Check(goal, ConductorAutonomyPolicy.Conservative, GoalLifecycleState.WorkspaceReady));
        Assert.Equal(1, runner.Calls);
    }
}
