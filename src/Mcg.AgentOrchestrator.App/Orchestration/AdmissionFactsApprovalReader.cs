using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AdmissionFactsApprovalReader
{
    internal static LandingDenylistOwnerGate.Result Read(ConductorDriver driver,
        AcceptanceCohortReceipt receipt, AcceptanceCohortMemberBinding member)
    {
        if (driver.AdmissionFacts.Active)
        {
            var facts = driver.AdmissionFacts.Reader!.Facts(member.GoalId);
            if (facts is null || facts.Inputs.Branch != member.CandidateRevision ||
                facts.Inputs.Main != receipt.Identity.ObservedMainRevision ||
                !facts.Scope.Files.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(member.LandingPaths))
                throw new InvalidOperationException("Receipt admission facts are not current.");
            return facts.Approval;
        }
        var denylist = driver.ApprovalHoldCache.LoadDenylist(driver.ExecutionDirectory, receipt.Identity.ObservedMainRevision);
        var decisions = CollaborationItemStore.ForDirectory(OrchestratorWorkspace.ForDirectory(driver.ExecutionDirectory).OrchestratorDirectory);
        return driver.ApprovalHoldCache.Evaluate(driver.ExecutionDirectory, denylist, decisions, member.GoalId.Value,
            receipt.Identity.ObservedMainRevision, member.CandidateRevision, member.LandingPaths);
    }
}
