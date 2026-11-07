using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: receipt tampering and landing operate only on a scenario-owned repository.
public sealed class MergeTrainFocusedRecheckReceiptContractTests : MergeTrainFocusedRecheckReceiptSupport
{
    [Theory]
    [InlineData("bisection")]
    [InlineData("stack-frame")]
    public void AttributionSourceRetainsTheExistingCompositeReceiptContract(string source)
    {
        MergeTrainFocusedRecheckTests.WithTrain("green", true, train =>
        {
            var receipt = train.Result.Receipt!;
            var proof = receipt.Recheck! with { AttributionSource = source };
            var bisection = receipt with
            {
                Recheck = proof,
                ReceiptId = MergeTrainFocusedRecheckReceipt.ReceiptId(receipt.Identity, proof)
            };
            Assert.True(MergeTrainFocusedRecheckReceiptAcceptance.Accepts(bisection, train.Store));
            var invalid = proof with { AttributionSource = "unrecognized" };
            Assert.False(MergeTrainFocusedRecheckReceiptAcceptance.Accepts(bisection with
            {
                Recheck = invalid,
                ReceiptId = MergeTrainFocusedRecheckReceipt.ReceiptId(receipt.Identity, invalid)
            }, train.Store));
        });
    }

    [Fact]
    public void StoredComposite_CompleteProof_IsAcceptedByLanding()
    {
        MergeTrainFocusedRecheckTests.WithTrain("green", true, train =>
        {
            var receipt = train.Result.Receipt!;
            var readOnly = MergeTrainAcceptanceStore.OpenReadOnly(DatabasePath(train.Repo));
            var reloaded = Assert.IsType<MergeTrainReceipt>(readOnly.TryReadReceipt(receipt.Identity.Value));
            Assert.Equal(receipt.ReceiptId, reloaded.ReceiptId);
            Assert.Equal(receipt.GateAttemptId, reloaded.GateAttemptId);
            Assert.Equal(MergeTrainFocusedRecheckTests.ExpectedSelection, reloaded.Recheck!.Selection);
            Assert.True(MergeTrainFocusedRecheckReceiptAcceptance.Accepts(reloaded, readOnly));
            var result = LandingExecutor.ExecuteMergeTrain(train.Kernel, train.Goals.Take(2).ToArray(),
                OrchestratorWorkspace.ForDirectory(train.Repo), reloaded, train.TrainCommit!, train.Store,
                ConductorAutonomyPolicy.Permissive);
            Assert.Equal(AcceptanceCohortLandingOutcome.Advanced, result.Outcome);
            Assert.All(train.Goals.Take(2), goal => Assert.Equal(GoalStatus.Completed, goal.Status));
            Assert.Equal(reloaded.Identity.TrainTreeRevision, RunGitOutput(train.Repo, "rev-parse", "main^{tree}").Trim());
            Assert.Empty(train.Store.ReadFinalizedLandingTrainIds());
            // The driver acknowledges landing effects after the executor advances main.
            train.Store.CompleteLandingEffects(reloaded.Identity.Value, reloaded.ReceiptId);
            Assert.Equal(reloaded.Identity.Value, Assert.Single(train.Store.ReadFinalizedLandingTrainIds()));
            Assert.All(train.Goals.Take(2), goal =>
                Assert.Equal(reloaded.ReceiptId, Assert.Single(train.Store.ReadFinalizedLandingsForGoal(goal.Id)).ReceiptId));
        });
    }

    [Fact]
    public void OrdinaryGateReceipt_KeepsAuthoritativeEvidenceContract()
    {
        MergeTrainFocusedRecheckTests.WithTrain("green", true, train =>
        {
            var ordinary = train.Result.Receipt! with { Recheck = null, ReceiptId = "ordinary-gate-receipt" };
            Assert.False(MergeTrainFocusedRecheckReceiptAcceptance.IsComposite(ordinary));
            Assert.True(ordinary.HasAuthoritativeLandingEvidence);
            Assert.False((ordinary with { GateExitCode = 1 }).HasAuthoritativeLandingEvidence);
            Assert.False((ordinary with { ValidForLanding = false }).HasAuthoritativeLandingEvidence);
            Assert.False((ordinary with { GateEvidenceArtifacts = [] }).HasAuthoritativeLandingEvidence);
        });
    }
}
