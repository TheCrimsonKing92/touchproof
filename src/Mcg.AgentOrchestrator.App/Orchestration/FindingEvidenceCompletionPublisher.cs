using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingEvidenceCompletionPublisher
{
    internal static void Publish(ConductorDriver driver, Goal goal, TaskSpec task, FindingEvidenceReceipt receipt,
        IReadOnlyList<(ReviewFinding Finding, PreTesterCoverageComposition.Completion Completion)> completions,
        string evidenceSummary, ConductorParallelAcceptanceAttempt? attemptToReconcile)
    {
        void Record()
        {
            driver._recordFindingEvidenceRequestDispositions(goal.Id, task.Id, receipt.RequestDispositions!);
            foreach (var (finding, completion) in completions)
            {
                var outcome = completion.Outcome;
                foreach (var contributingReceipt in completion.ContributingReceipts)
                {
                    driver._recordFindingEvidenceOutcome(goal.Id, task.Id, finding.StableId, outcome,
                        contributingReceipt.ReceiptId == receipt.ReceiptId ? receipt : contributingReceipt);
                }
                driver._recordFindingEvidenceRequest(
                    goal.Id, task.Id,
                    $"finding-evidence disposition=honoured; role={task.RequiredRole}; task_id={task.Id}; " +
                    $"finding_id={finding.StableId}; candidate_sha={receipt.CandidateSha}; receipt_id={receipt.ReceiptId}; reason={completion.ExecutionResultReason}; " +
                    $"decision={outcome.DecisionReason}; source_receipt_ids={string.Join(',', outcome.SourceReceiptIds!)}");
                driver._recordFindingEvidenceRun(
                    goal.Id, task.Id,
                    $"finding-evidence role={task.RequiredRole}; task_id={task.Id}; finding_id={finding.StableId}; " +
                    $"candidate_sha={receipt.CandidateSha}; receipt_id={receipt.ReceiptId}; execution_basis={receipt.ExecutionBasisIdentity}; reason={completion.ExecutionResultReason}; {evidenceSummary}");
            }
        }

        if (attemptToReconcile is null)
        {
            Record();
            return;
        }

        // The green attempt remains replayable until its receipt and run charges share a durable snapshot.
        FindingEvidenceCompletionPublication.Apply(goal, task, Record,
            () => driver.CheckpointFindingEvidenceCompletion?.Invoke(goal.Id));
        driver.FocusedEvidenceAttemptCoordinator.MarkReconciled(attemptToReconcile);
    }
}
