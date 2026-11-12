using Mcg.AgentOrchestrator.App.Application;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Maps recorded-dispatch process results to conductor start outcomes.</summary>
internal static class RecordedDispatchStartClassifier
{
    internal static DispatchStartOutcome Classify(ProcessBatchExecutionResult result)
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

        return DispatchStartOutcome.EmptyBatch(DispatchStartRefusalReasonBuilder.Build(result));
    }
}
