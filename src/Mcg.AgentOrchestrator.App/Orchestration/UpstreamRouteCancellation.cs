using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class UpstreamRouteCancellation
{
    internal static void Cancel(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec upstream,
        Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord> cancelLatestProcess,
        Action? markGoalMutated)
    {
        var upstreamOrder = SdlcRoleOrder(upstream.RequiredRole);
        var downstreamIds = goal.Tasks
            .Where(task => SdlcRoleOrder(task.RequiredRole) > upstreamOrder && task.LastProcess is { IsRunning: true })
            .Select(task => task.Id)
            .ToArray();

        foreach (var taskId in downstreamIds)
        {
            var task = kernel.GetTask(goal.Id, taskId);
            if (task.LastProcess is not { IsRunning: true })
                continue;

            var cancelled = cancelLatestProcess(kernel, goal.Id, taskId);
            if (cancelled is { WasCancelled: true, IsRunning: false } && task.LastProcess == cancelled)
            {
                // Checkpoint this cancellation even if a later cancellation or the retry fails.
                markGoalMutated?.Invoke();
                kernel.RecordTaskNote(goal.Id, taskId,
                    $"Dispatch cancelled because an upstream route superseded it (task '{upstream.Id}').");
            }
        }
    }

    // Match the retry guard's role ordering in AgentOrchestratorKernel.GoalLifecycle.cs.
    private static int SdlcRoleOrder(AgentRole role) => role switch
    {
        AgentRole.Researcher => 0,
        AgentRole.Planner => 1,
        AgentRole.Ideation => 2,
        AgentRole.Developer => 3,
        AgentRole.Tester => 4,
        AgentRole.Reviewer => 5,
        _ => int.MaxValue
    };
}
