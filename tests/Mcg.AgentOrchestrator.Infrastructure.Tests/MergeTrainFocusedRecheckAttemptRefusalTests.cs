using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: receipt tampering and landing operate only on a scenario-owned repository.
public sealed class MergeTrainFocusedRecheckAttemptRefusalTests : MergeTrainFocusedRecheckReceiptSupport
{
    [Theory]
    [InlineData("recheck-reference")]
    [InlineData("recheck-wrong-attempt")]
    [InlineData("recheck-main")]
    [InlineData("recheck-tree")]
    [InlineData("unattributed")]
    public void IncompleteOrMismatchedProof_IsRefusedBeforePreparingLanding(string defect)
        => AssertIncompleteOrMismatchedProofRefused(defect);
}
