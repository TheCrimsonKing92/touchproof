using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;

// Each scenario owns its repository and stores; isolate the shared landing git hooks.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PassedCohortOwnerApprovalPartnerReleaseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PassedReceipt_PendingOwnerApproval_RequeuesOnlyOrdinaryPartner(int heldIndex)
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario(heldIndex);
        var heldGoal = scenario.Goals[heldIndex];
        var partner = scenario.Goals[1 - heldIndex];
        Assert.True(scenario.Receipt.HasAuthoritativeLandingEvidence);
        Assert.Equal(2, scenario.Receipt.Identity.Members.Count);
        Assert.Equal(1, scenario.Verifier.RunCount);
        var candidates = scenario.Goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id,
            scenario.Driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive,
                scenario.Kernel))).ToArray();
        Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(candidates[heldIndex].ProjectionResult);
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(candidates[1 - heldIndex].ProjectionResult);
        var results = new Dictionary<string, ParallelLandingOutcome>();
        var progress = new List<string>();

        var (eligible, admittedCandidates) = PassedCohortReceiptApprovalHoldAdmission.Apply(
            scenario.Driver, ConductorAutonomyPolicy.Permissive, scenario.Kernel,
            scenario.Goals, candidates, scenario.Goals, new HashSet<GoalId>(), _ => true,
            new HashSet<string>(), results, 1, progress.Add);

        Assert.Equal(partner.Id, Assert.Single(eligible).Id);
        var admitted = Assert.Single(admittedCandidates);
        Assert.Equal(partner.Id, admitted.GoalId);
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(admitted.ProjectionResult);
        Assert.Equal(heldGoal.Id.Value, Assert.Single(results).Key);
        Assert.True(PassedCohortReceiptApprovalHold.IsReadOnlyReservation(results[heldGoal.Id.Value].Result));
        Assert.Contains(progress, line => line.StartsWith("COHORT_RECEIPT_RELEASED ", StringComparison.Ordinal) &&
            line.Contains($"receipt={scenario.Receipt.ReceiptId}", StringComparison.Ordinal) &&
            line.Contains($"held={heldGoal.Id.Value[..8]} released={partner.Id.Value[..8]} path=requeue",
                StringComparison.Ordinal));
        Assert.Null(ConductorAcceptanceCohortSelector.Select(candidates).Selection);
        Assert.Empty(ConductorSpeculativeAcceptanceCohortPlanner.Plan(candidates).Cohorts);
        Assert.Empty(scenario.Driver.FindLandablePassedCohortSelections(admittedCandidates));
        Assert.Equal(1, scenario.Verifier.RunCount);
        Assert.All(scenario.Goals, goal =>
        {
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Empty(scenario.Store.ReadFinalizedLandingsForGoal(goal.Id));
            AssertNoLanded(scenario.Workspace, goal.Id);
        });

        // Requeue is an admission decision, so the original complete receipt remains usable
        // when approval arrives before either member starts another gate or changes main.
        Approve(scenario.Workspace, heldGoal, scenario.CandidateRevisions[heldIndex]);
        Assert.IsType<GateReadyCandidateProjectionResult.Ready>(scenario.Driver.ProjectGroupedAdmissionCandidate(
            heldGoal, ConductorAutonomyPolicy.Permissive, scenario.Kernel));
        var approvedProgress = scenario.Tick();
        Assert.Contains(approvedProgress, line => line.Contains(
            $"prelanded=true outcome=passed receipt={scenario.Receipt.ReceiptId}", StringComparison.Ordinal));
        Assert.All(scenario.Goals, goal =>
        {
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Equal(scenario.Receipt.ReceiptId,
                Assert.Single(scenario.Store.ReadFinalizedLandingsForGoal(goal.Id)).ReceiptId);
        });
        Assert.Equal(1, scenario.Verifier.RunCount);
    }
}
