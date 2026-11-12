using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A deliberately held preparation still belongs to its start-path owner, even without a process.
internal static class PreparedDispatchRecoveryEligibility
{
    internal static bool IsHeld(ConductorDriver driver, Goal goal, TaskSpec task, OrchestratorWorkspace? workspace)
    {
        // Operator intent suppression discards its preparation in StartDispatches; it does not
        // leave a Running preparation to preserve here.
        if (task.LastDispatch?.FindingEvidenceReviewContinuation is not null &&
            FindingEvidenceContinuationCoordinator.FindingEvidenceContinuationInvalid(driver, goal, task))
            return true;
        if (GoalDispatchOperations.RequiresRetiredPreparedDispatchRefresh(task)) return true;
        return workspace is not null &&
            GoalDispatchOperations.PreparedDeveloperTestLaneBlocker(goal, workspace, task) is not null;
    }
}
