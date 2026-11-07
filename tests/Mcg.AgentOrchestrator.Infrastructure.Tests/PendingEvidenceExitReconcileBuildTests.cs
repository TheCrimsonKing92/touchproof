using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ExitReconcileBuildControlSupport;

// Parallel-safe: all negative stimuli affect only fixture-owned files and canonical state.
public sealed class PendingEvidenceExitReconcileBuildTests
{
    [Theory]
    [InlineData("missing", "missing-current-candidate-outcome")]
    [InlineData("tree", "missing-current-candidate-outcome")]
    public async Task TerminalSweep_InapplicableBuild_RejectsWithoutConsumption(
        string control, string diagnostic)
        => await ExitReconcileBuildControlSupport.TerminalSweep_InapplicableBuild_RejectsWithoutConsumption(control, diagnostic);
}
