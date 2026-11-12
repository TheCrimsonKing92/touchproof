using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Owns recovery and the durable streak of retry-admission start aborts.</summary>
internal static class AbortedDispatchStartRecovery
{
    internal const string NotePrefix = "DISPATCH_START_ABORTED retry-admission: ";
    internal const string RepeatedReason = "dispatch-start-aborted-repeatedly";

    internal static RetryAdmissionResult Admit(AgentOrchestratorKernel kernel, GoalId goalId,
        TaskId taskId, bool checkpointCommitted, Func<RetryAdmissionResult> admit)
    {
        var dispatchAt = kernel.GetTask(goalId, taskId).LastDispatch?.DispatchedAt;
        try
        {
            return admit();
        }
        catch (Exception error) when (checkpointCommitted && error is not OperationCanceledException &&
            dispatchAt is not null && CanDiscard(kernel.GetTask(goalId, taskId), dispatchAt.Value))
        {
            DispatchStartFailureEvidence.Record(kernel, goalId, taskId, dispatchAt.Value, error.Message);
            kernel.DiscardUnstartedPreparedDispatch(goalId, taskId, dispatchAt.Value, NotePrefix + error.Message);
            var reason = IsRepeated(kernel.GetGoal(goalId), taskId)
                ? DescribeRepeated()
                : $"Retry admission start aborted; the unstarted dispatch was discarded: {error.Message}";
            throw new AbortedDispatchStartException(reason, error);
        }
    }

    private static bool CanDiscard(TaskSpec task, DateTimeOffset dispatchAt) =>
        task.LastDispatch?.DispatchedAt == dispatchAt && NeverStartedDispatchReconciliation.IsUnclaimed(task);

    internal static bool IsRepeated(Goal goal, TaskId taskId)
    {
        if (goal.FindTask(taskId).Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled)
            return false;

        string? failure = null;
        var count = 0;
        // Timeline order, rather than wall-clock order, also works when events share a timestamp.
        foreach (var entry in goal.Timeline.Reverse())
        {
            if (entry.Kind == ProgressKind.TaskNote && entry.Message.StartsWith(NotePrefix, StringComparison.Ordinal))
            {
                if (entry.TaskId != taskId) break;
                var text = entry.Message[NotePrefix.Length..];
                if (failure is not null && !string.Equals(text, failure, StringComparison.Ordinal)) break;
                failure = text;
                count++;
                if (count == 2) return true;
            }
            if (entry.TaskId == taskId &&
                (entry.Kind is ProgressKind.TaskProcessStarted or ProgressKind.TaskRetried ||
                 entry.Message.StartsWith("OPERATOR_ROUTING_NOTE ", StringComparison.Ordinal))) break;
            if (entry.OperatorIntentApplied is { Outcome: "applied" } applied && applied.TaskId == taskId.Value &&
                (applied.Verb == OperatorIntentVerbs.Retry ||
                 applied.Verb == OperatorIntentVerbs.Adjudicate && applied.RetryRoundKind == RetryRoundKind.Standard)) break;
        }
        return false;
    }

    internal static string DescribeRepeated()
    {
        var next = SoloHoldNextAction.For(RepeatedReason);
        return $"{RepeatedReason}; mechanism: {next.Mechanism}; owner: {next.Owner}; next action: {next.NextAction}";
    }
}
