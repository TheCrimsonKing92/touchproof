using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ExitReconcileBuildControlSupport;

// Parallel-safe: all negative stimuli affect only fixture-owned files and canonical state.
public sealed class DispatchIdentityRejectionExitReconcileBuildTests
{
    [Theory]
    [InlineData("prior-dispatch", "history-invalid")]
    [InlineData("other-dispatch", "history-invalid")]
    [InlineData("worktree", "history-invalid")]
    public async Task TerminalSweep_InapplicableBuild_RejectsWithoutConsumption(
        string control, string diagnostic)
        => await ExitReconcileBuildControlSupport.TerminalSweep_InapplicableBuild_RejectsWithoutConsumption(control, diagnostic);
}
