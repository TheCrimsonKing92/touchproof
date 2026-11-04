namespace Mcg.AgentOrchestrator.Core;

// Owns dispatch provenance and protocol rejection for completed Planner evidence.
internal static class PlannerEvidenceCompletion
{
    // Historical directive provenance survives retry; only the current accepted
    // round can authorize execution deferral or lifecycle reconciliation.
    internal static bool TryGetCurrentAcceptedBatch(Goal goal, TaskSpec task,
        out HumanInputDirectiveParseResult? batch, out int? briefVersion, out DateTimeOffset recordedAt)
    {
        batch = null;
        briefVersion = null;
        recordedAt = default;
        if (task.RequiredRole != AgentRole.Planner || task.Status != WorkTaskStatus.Completed) return false;
        if (task.LastDispatch is { } dispatch &&
            task.LastVerification is { Succeeded: true, PlannerEvidenceBatch: { } stored } verification &&
            verification.DispatchAttemptAt == dispatch.DispatchedAt &&
            WorkerResultBlockers.AssessSubscriptionVerification(task, verification).AllowsCompletion &&
            (verification.CompletionVerdictVerifiedSuccess ||
             DispatchFailureClassifier.Classify(task, verification).Kind == DispatchOutcomeKind.VerifiedSuccess) &&
            ValidateProvenance(task, verification, out briefVersion) is null)
        {
            batch = stored;
            recordedAt = verification.CompletedAt;
        }
        else if (task.LastDispatch is null && task.LastExecution is { PlannerEvidenceBriefVersion: not null } execution &&
            !OutputTokenLimit.IsHit(execution) && WorkerResultBlockers.HasCompleteWorkerResult(execution.AuthoritativeOutput ?? "") &&
            !WorkerResultBlockers.TryFindBlocker(execution.AuthoritativeOutput ?? "", out _))
        {
            briefVersion = execution.PlannerEvidenceBriefVersion;
            batch = AgentOutputDirectives.ParseHumanInputRequest(execution.AuthoritativeOutput ?? "", AgentRole.Planner);
            recordedAt = execution.CompletedAt;
        }
        return batch is not null && briefVersion == goal.AuthoritativeBrief.Version;
    }

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
