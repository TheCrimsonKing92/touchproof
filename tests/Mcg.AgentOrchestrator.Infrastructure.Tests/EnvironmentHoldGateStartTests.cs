using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

public sealed class EnvironmentHoldGateStartTests
{
    [Fact]
    public void FailingProbe_BlocksInlineGate_AndHoldSurvivesVerifyingTracker()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var runs = 0;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceSummary: _ => { runs++; return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; });
        driver.EnvironmentGate = new(kernel, "project", Path.GetTempPath(), () => EnvironmentContractTestFixture.Contract,
            new EnvironmentContractTestFixture.Runner(), _ => EnvironmentContractTestFixture.Environment());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(ConductorHoldOwner.Environment, hold.Owner);
        Assert.Equal(0, runs);
        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, hold with { State = GoalLifecycleState.Verifying },
            DateTimeOffset.UtcNow, TimeSpan.Zero, [], []);
        Assert.Equal(EnvironmentHold.State, goal.CurrentHold?.State);
    }
}
