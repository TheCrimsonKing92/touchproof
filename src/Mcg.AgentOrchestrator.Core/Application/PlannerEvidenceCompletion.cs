namespace Mcg.AgentOrchestrator.Core;

// Owns dispatch provenance and protocol rejection for completed Planner evidence.
internal static class PlannerEvidenceCompletion
{
    internal static string? ValidateProvenance(TaskSpec task, TaskVerificationRecord verification,
        out int? briefVersion)
    {
        var evidenceDispatch = verification.DispatchAttemptAt is { } at
            ? task.DispatchHistory.LastOrDefault(item => item.DispatchedAt == at) : null;
        briefVersion = evidenceDispatch?.BriefSnapshot is not null ? evidenceDispatch.BriefVersion : null;
        if (verification.DispatchAttemptAt is not null && evidenceDispatch is null)
            return "Planner evidence has unknown dispatch provenance; replan.";
        if (evidenceDispatch is not null && evidenceDispatch.DispatchedAt != task.LastDispatch!.DispatchedAt)
            return "Planner evidence completion belongs to an obsolete dispatch; replan.";
        return null;
    }

    internal static void Reject(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        TaskVerificationRecord verification, string diagnostic)
    {
        // Preserve the observed process exit. A protocol rejection is nevertheless not passing
        // verification and must never authorize completion when an unrelated answer arrives.
        task.RecordVerification(verification with { OrchestratorFailureReason = diagnostic, CompletionVerdictVerifiedSuccess = false, CompletionVerdictRule = TaskOutcomeRules.PlannerOutputContractRejected.Token });
        kernel.ReportWorkerTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, diagnostic);
    }
}
