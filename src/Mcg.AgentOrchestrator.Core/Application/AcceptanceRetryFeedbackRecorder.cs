namespace Mcg.AgentOrchestrator.Core;

/// <summary>Records criterion feedback with both task and goal retry accounting.</summary>
internal static class AcceptanceRetryFeedbackRecorder
{
    internal static int Record(Goal goal, TaskId taskId, IReadOnlyList<string> feedback)
    {
        var task = goal.FindTask(taskId);
        task.RecordCriterionRetryFeedback(feedback);
        task.IncrementCriterionRetryCount();
        goal.IncrementAutomaticAcceptanceRetryCount();
        return goal.AutomaticAcceptanceRetryCount;
    }
}
