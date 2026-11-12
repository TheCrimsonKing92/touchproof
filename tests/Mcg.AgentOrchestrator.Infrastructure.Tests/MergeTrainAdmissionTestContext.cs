using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

internal static class MergeTrainAdmissionTestContext
{
    // Unlike cohorts, trains require a refined spec to establish operator evidence obligations.
    internal static void Refine(AgentOrchestratorKernel kernel, params Goal[] goals)
    {
        foreach (var goal in goals)
        {
            Assert.Null(goal.AuthoritativeRefinedSpecVersion);
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["Automated verification passes"], VerificationClass.TestVerifiable, [], [])
            {
                AcceptanceGateOwnedAcceptanceCriteria = ["Automated verification passes"]
            });
            Assert.Null(AcceptanceCriterionEvidence.DescribeTrainOperatorEvidenceGap(goal));
            Assert.DoesNotContain(goal.LandingPrerequisiteObligations,
                obligation => obligation.Owner != CriterionEvidenceOwner.Acceptance);
        }
    }
}
