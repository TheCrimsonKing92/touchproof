using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Validates the immediate task state captured before preparing a retry dispatch.</summary>
internal static class RetryAdmissionReplayBaseline
{
    internal static bool IsValid(TaskSnapshot task, TaskId taskId, DateTimeOffset retriedAt,
        TaskDispatchRecord prepared)
    {
        if (task.Id != taskId.Value || task.LatestRetryAt != retriedAt ||
            task.Status is not (WorkTaskStatus.Pending or WorkTaskStatus.Assigned))
            return false;

        if (task.LastDispatch is { } previous)
        {
            if (previous.DispatchedAt >= prepared.DispatchedAt)
                return false;
            if (previous.DispatchedAt > retriedAt)
                return task.LastProcess is { CompletedAt: { } completed, ExitCode: not null } &&
                    completed < prepared.DispatchedAt;
        }

        // Pre-marker baselines retain their existing settlement boundary.
        return task.LastProcess is null ||
            task.LastDispatch is not null &&
            task.LastProcess is { CompletedAt: { } ended, ExitCode: not null } && ended <= retriedAt;
    }
}
