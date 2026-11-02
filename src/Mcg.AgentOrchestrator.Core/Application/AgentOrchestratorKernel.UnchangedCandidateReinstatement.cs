using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public UnchangedCandidateReinstatement? ReinstateUnchangedCandidateVerdict(
        GoalId goalId, TaskId taskId, CandidateIdentity current)
    {
        var goal = GetGoal(goalId);
        var task = goal.FindTask(taskId);
        var reinstatement = UnchangedCandidateRule.EvaluateReinstatement(goal, task, current);
        if (reinstatement is null) return null;

        EnsureGoalCanResumeWork(goal);
        TaskStatusTransitionRecorder.Record(_eventWriter, goal.Id, task.ReinstateVerification(reinstatement.PriorVerification), TaskStatusTransitionRecorder.UnchangedCandidateReinstatementReinstateUnchangedCandidateVerdict);
        Append(goal, taskId, ProgressKind.TaskUpdated, reinstatement.Render());
        RefreshGoalStatus(goal);
        return reinstatement;
    }
}
