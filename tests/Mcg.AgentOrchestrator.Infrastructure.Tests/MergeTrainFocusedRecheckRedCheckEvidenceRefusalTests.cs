using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: receipt tampering and landing operate only on a scenario-owned repository.
public sealed class MergeTrainFocusedRecheckRedCheckEvidenceRefusalTests : MergeTrainFocusedRecheckReceiptSupport
{
    [Theory]
    [InlineData("red-check-evidence-missing")]
    [InlineData("red-check-evidence-incomplete")]
    [InlineData("red-check-unexplained")]
    [InlineData("red-check-name")]
    [InlineData("red-check-identity")]
    public void IncompleteOrMismatchedProof_IsRefusedBeforePreparingLanding(string defect)
        => AssertIncompleteOrMismatchedProofRefused(defect);
}
