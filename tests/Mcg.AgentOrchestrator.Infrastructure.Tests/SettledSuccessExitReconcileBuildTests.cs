using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ExitReconcileBuildControlSupport;

// Parallel-safe: all negative stimuli affect only fixture-owned files and canonical state.
public sealed class SettledSuccessExitReconcileBuildTests
{
    [Theory]
    [InlineData("batch", false)]
    [InlineData("batch", true)]
    [InlineData("preflight", false)]
    [InlineData("preflight", true)]
    public async Task OrderedReconcilers_SettledSuccess_IsConsumedAndCompletedOnce(
        string other, bool terminalFirst)
    {
        using var fixture = new ExitReconcileBuildFixture();
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        int Other() => other == "batch" ? fixture.BatchRecovery() : fixture.DispatchPreflight();

        Assert.Equal(1, terminalFirst ? fixture.TerminalSweep() : Other());
        fixture.AssertSuccess(invocation);
        var completion = Assert.Single(fixture.Task.VerificationHistory);
        var head = fixture.Build.Git("rev-parse", "HEAD");
        fixture.Save();
        fixture.Reload();

        Assert.Equal(0, terminalFirst ? Other() : fixture.TerminalSweep());
        // Repeating the same entry point is also inert after reloading its settled state.
        Assert.Equal(0, fixture.TerminalSweep());

        fixture.AssertSuccess(invocation);
        Assert.Equal(completion.CompletedAt, Assert.Single(fixture.Task.VerificationHistory).CompletedAt);
        Assert.Equal(head, fixture.Build.Git("rev-parse", "HEAD"));
        Assert.Single(fixture.Goal.Timeline.Where(evt => evt.TaskId == fixture.Build.TaskId &&
            evt.Kind == ProgressKind.TaskCompleted));
    }
}
