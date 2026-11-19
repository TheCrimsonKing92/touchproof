namespace Mcg.AgentOrchestrator.Core;

internal static class ProviderHoldRetry
{
    private static readonly TimeSpan UnparsedLimitDelay = TimeSpan.FromMinutes(5);

    internal static bool IsHoldInterruption(TaskVerificationRecord verification) =>
        verification.CompletionVerdictRule == TaskOutcomeRules.ProviderHoldCheckInconclusive.Token ||
        ProviderHoldEvidence.Read(verification).Confirmed;

    internal static bool TryRequeueInconclusiveCheck(Goal goal, TaskSpec task, TaskVerificationRecord verification,
        DispatchOutcome outcome, DateTimeOffset now, IGoalLifecycleEventWriter eventWriter)
    {
        if (outcome.Kind == DispatchOutcomeKind.VerifiedSuccess || task.LastDispatch is not { } dispatch ||
            !ProviderBudgetHoldSchedules.IsAdmittedProbe(goal, task, dispatch) ||
            DispatchCompletionEvidence.HasWorkerEvidenceThatOutranksSubscriptionLimit(
                verification, verification.WorkerResultPresent, verification.HasCommittedChanges) ||
            ProviderHoldEvidence.Read(verification).Confirmed) return false;
        task.RecordCompletionVerdict(false, TaskOutcomeRules.ProviderHoldCheckInconclusive.Token);
        PrepareRetry(task, now);
        TaskStatusTransitionRecorder.Record(eventWriter, goal.Id,
            task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned), TaskStatusTransitionRecorder.RecordingRecordDispatchExecutionResultProviderHoldCheckInconclusive);
        return true;
    }

    internal static string RequeueSubscriptionLimit(Goal goal, TaskSpec task, TaskVerificationRecord verification,
        DateTimeOffset now, IGoalLifecycleEventWriter eventWriter)
    {
        var evidence = ProviderHoldEvidence.Read(verification);
        var retryAfter = evidence.ResetAt ??
            (!evidence.Confirmed && ProviderLimitSignals.TryGetSubscriptionLimitRetryAfter(verification, out var inferred)
                ? inferred : now + UnparsedLimitDelay);
        task.SetSubscriptionRetryAfter(retryAfter);
        var failureCount = SubscriptionLimitEvidence.CountRecoverableSubscriptionLimitFailures(task, includeProviderHolds: false);
        PrepareRetry(task, now);
        TaskStatusTransitionRecorder.Record(eventWriter, goal.Id,
            task.SetStatus(task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned), TaskStatusTransitionRecorder.RecordingRecordDispatchExecutionResultSubscriptionLimit);
        return !evidence.Confirmed && failureCount >= DispatchFailureClassifier.RecoverableSubscriptionLimitReviewThreshold
            ? $"Dispatch hit a recoverable subscription usage limit {failureCount} time(s); review model, profile, or timing before redispatch: {task.LastDispatch!.Command}"
            : $"Dispatch hit a recoverable subscription usage limit; task is ready to retry later: {task.LastDispatch!.Command}";
    }

    private static void PrepareRetry(TaskSpec task, DateTimeOffset now)
    {
        task.RecordRetry(now, retryCause: RetryCause.ProviderInterruption);
        task.ClearLatestVerification();
    }
}
