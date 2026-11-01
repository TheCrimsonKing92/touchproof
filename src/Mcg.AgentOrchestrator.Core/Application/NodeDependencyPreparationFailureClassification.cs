namespace Mcg.AgentOrchestrator.Core;

internal static class NodeDependencyPreparationFailureClassification
{
    internal static bool TryClassify(TaskSpec task, TaskVerificationRecord verification,
        bool workerResultPresent, bool hasCommittedChanges, out DispatchOutcome outcome)
    {
        // A failed preparation never starts a worker, so it cannot supply a terminal worker result.
        // Keep this new environment fault ahead of that missing-terminal-contract diagnosis.
        return WorkerSandboxRefusal.TryClassifyWorkerSandboxRefusal(
            task, verification, workerResultPresent, hasCommittedChanges, out outcome) &&
            outcome.EvidenceSummary.StartsWith(
                "worker sandbox refused: reason=node-dependency-preparation-failed path=", StringComparison.Ordinal);
    }
}
