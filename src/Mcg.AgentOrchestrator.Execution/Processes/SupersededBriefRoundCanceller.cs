using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class SupersededBriefRoundCanceller(BackgroundDispatchRunner runner)
{
    public void CancelAndRequeueInterruptedDispatches(
        AgentOrchestratorKernel kernel,
        Func<GoalId, TaskId, InterruptedDispatchStateRead>? readCurrentState = null,
        GoalId? onlyGoalId = null)
    {
        try
        {
            CancelSupersededRounds(kernel, onlyGoalId);
        }
        finally
        {
            runner.RequeueInterruptedDispatches(kernel, readCurrentState);
        }
    }

    public int CancelSupersededRounds(AgentOrchestratorKernel kernel, GoalId? onlyGoalId = null)
    {
        var cancelled = 0;
        foreach (var goal in kernel.Goals.Where(goal => goal.Status == GoalStatus.Active &&
                     (onlyGoalId is null || goal.Id == onlyGoalId)).ToArray())
        {
            foreach (var task in goal.Tasks.ToArray())
            {
                var currentVersion = goal.AuthoritativeBrief.Version;
                if (goal.Status != GoalStatus.Active ||
                    task.Status != WorkTaskStatus.Running ||
                    task.RequiredRole is not (AgentRole.Planner or AgentRole.Researcher) ||
                    task.LastProcess is not { IsRunning: true } ||
                    task.LastDispatch is not { BriefSnapshot: not null } dispatch ||
                    dispatch.BriefVersion == currentVersion)
                {
                    continue;
                }

                runner.CancelLatestProcessByConductor(kernel, goal.Id, task.Id);
                kernel.RecordTaskNote(goal.Id, task.Id,
                    $"Brief revision cancelled this round: dispatch brief v{dispatch.BriefVersion} " +
                    $"superseded by current brief v{currentVersion}; conductor-cancelled for automatic re-queue.");
                cancelled++;
            }
        }

        return cancelled;
    }
}
