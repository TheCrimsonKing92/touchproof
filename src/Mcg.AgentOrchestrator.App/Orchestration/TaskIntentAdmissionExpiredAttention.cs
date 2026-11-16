using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class TaskIntentAdmissionExpiredAttention
{
    internal const string Prefix = "task-intent-admission-expired:";

    internal static CollaborationItem Raise(ICollaborationItemStore store, Goal goal, OperatorIntentRecord intent) =>
        store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value,
            $"{intent.Verb}: task-intent-admission-expired",
            $"Operator intent {intent.Id} ({intent.Verb}) was rejected: task-intent-admission-expired. " +
            $"{DescribeTaskTarget(goal, intent.TaskId)} The intent was not applied. Resubmit it if still wanted.",
            Prefix + intent.Id).GetAwaiter().GetResult();

    private static string DescribeTaskTarget(Goal goal, string? taskId)
    {
        if (taskId is null)
            return "No task target is present.";

        for (var index = 0; index < goal.Tasks.Count; index++)
            if (goal.Tasks[index].Id.Value == taskId)
                return $"Task {index + 1} ({taskId}).";

        return $"Task target {taskId} is not in the current task list.";
    }
}
