using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ExitReconcileBuildControlSupport;

// Parallel-safe: all negative stimuli affect only fixture-owned files and canonical state.
public sealed class InvocationRecordRejectionExitReconcileBuildTests
{
    [Theory]
    [InlineData("scope", "scope-invalid")]
    [InlineData("coverage", "history-invalid")]
    [InlineData("invalid-apparatus", "history-invalid")]
    public async Task TerminalSweep_InapplicableBuild_RejectsWithoutConsumption(
        string control, string diagnostic)
        => await ExitReconcileBuildControlSupport.TerminalSweep_InapplicableBuild_RejectsWithoutConsumption(control, diagnostic);

    [Fact]
    public async Task TerminalSweep_CompilerFailure_RemainsCandidateFailure()
    {
        using var fixture = new ExitReconcileBuildFixture(compilerFailure: true);
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.CompilerFailure, invocation.Outcome);

        Assert.Equal(1, fixture.TerminalSweep());

        AssertRejected(fixture, "outcome=CompilerFailure");
        Assert.Null(fixture.Task.LastVerification!.OrchestratorFailureReason);
        Assert.True(fixture.StoredInvocation().Consumed);
        Assert.Equal(invocation.Version + 1, fixture.StoredInvocation().Version);
    }
}
