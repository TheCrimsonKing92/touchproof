using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: receipt tampering and landing operate only on a scenario-owned repository.
public sealed class MergeTrainFocusedRecheckRedReceiptRefusalTests : MergeTrainFocusedRecheckReceiptSupport
{
    [Theory]
    [InlineData("red-reference")]
    [InlineData("red-absent")]
    [InlineData("red-not-failed")]
    [InlineData("red-wrong-receipt")]
    [InlineData("red-wrong-attempt")]
    public void IncompleteOrMismatchedProof_IsRefusedBeforePreparingLanding(string defect)
        => AssertIncompleteOrMismatchedProofRefused(defect);
}
