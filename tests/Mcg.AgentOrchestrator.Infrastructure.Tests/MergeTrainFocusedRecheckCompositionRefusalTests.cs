using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: receipt tampering and landing operate only on a scenario-owned repository.
public sealed class MergeTrainFocusedRecheckCompositionRefusalTests : MergeTrainFocusedRecheckReceiptSupport
{
    [Theory]
    [InlineData("composition-order")]
    [InlineData("composition-member")]
    [InlineData("composition-candidate")]
    [InlineData("composition-main")]
    [InlineData("disjoint-absent")]
    [InlineData("disjoint-failed")]
    [InlineData("paths-overlap")]
    public void IncompleteOrMismatchedProof_IsRefusedBeforePreparingLanding(string defect)
        => AssertIncompleteOrMismatchedProofRefused(defect);
}
