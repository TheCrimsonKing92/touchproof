using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeferredNoChangeEvidenceOwner
{
    internal static string? Resolve(Goal goal, TaskId developerId, string candidateSha, out TaskSpec owner)
    {
        owner = (goal.Tasks.FirstOrDefault(task => task.RequiredRole == AgentRole.Tester) ??
            goal.Tasks.FirstOrDefault(task => task.RequiredRole == AgentRole.Reviewer))!;
        return owner is not null ? null :
            $"DEFERRED_NO_CHANGE_EVIDENCE_OWNER_ABSENT task={developerId.Value} candidate_sha={candidateSha}; " +
            "the goal has neither a Tester nor a Reviewer task";
    }
}
