namespace Mcg.AgentOrchestrator.Core;

// Decides whether an apparatus retry still precedes any worker acting on accepted feedback.
internal static class PendingRoutedFindingRetention
{
    internal static bool ShouldPreserve(TaskSpec task, RetryCause incomingCause) =>
        incomingCause is RetryCause.EnvironmentApparatusFailure or RetryCause.ProviderInterruption &&
        task.AcceptedRetryFeedback is { } feedback &&
        !task.RetryAdmissionHistory.Any(receipt =>
            receipt.LinkedDispatchAt >= feedback.AcceptedAt && receipt.WorkerStartedAt is not null);
}
