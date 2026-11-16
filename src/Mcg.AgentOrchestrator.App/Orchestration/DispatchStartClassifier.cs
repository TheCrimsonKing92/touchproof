using Mcg.AgentOrchestrator.App.Application;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Maps process results shared by subscription and recorded-dispatch starts.</summary>
internal static class DispatchStartClassifier
{
    internal static DispatchStartOutcome ClassifyRecordedStart(ProcessBatchExecutionResult result) =>
        ClassifyProcessStart(result) ?? DispatchStartOutcome.EmptyBatch(DispatchStartRefusalReasonBuilder.Build(result));

    // A result with no process outcome needs the caller's dispatch-preparation diagnostics.
    internal static DispatchStartOutcome? ClassifyProcessStart(ProcessBatchExecutionResult result)
    {
        if (result.RecoveryActions?.FirstOrDefault() is { } recoveryAction)
            return DispatchStartOutcome.RecoverableSandboxPrep(recoveryAction);
        if (result.Tasks.Count > 0)
            return DispatchStartOutcome.Started(result.Tasks);
        if (result.StartFailures?.FirstOrDefault() is { } startFailure)
            return DispatchStartOutcome.SpawnFailed(startFailure.Reason);

        if (result.RequeueSkippedCount > 0)
        {
            return DispatchStartOutcome.EmptyBatch(
                $"Skipped {result.RequeueSkippedCount} automatic interrupted-dispatch requeue(s) after terminal-state preflight");
        }

        return null;
    }
}
