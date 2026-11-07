using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: receipt tampering and landing operate only on a scenario-owned repository.
public sealed class MergeTrainFocusedRecheckSelectionRefusalTests : MergeTrainFocusedRecheckReceiptSupport
{
    [Theory]
    [InlineData("selection-missing-failure")]
    [InlineData("selection-missing-class")]
    [InlineData("execution-incomplete")]
    [InlineData("execution-not-run")]
    [InlineData("dependency-abandoned")]
    public void IncompleteOrMismatchedProof_IsRefusedBeforePreparingLanding(string defect)
        => AssertIncompleteOrMismatchedProofRefused(defect);
}
