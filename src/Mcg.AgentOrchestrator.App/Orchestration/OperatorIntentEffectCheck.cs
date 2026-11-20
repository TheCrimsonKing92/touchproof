using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OperatorIntentEffectCheck
{
    private static readonly string UnknownFailureExitPrefix = $"{TaskOutcomeRules.UnknownFailure.Token}; exit code ";

    internal static OperatorIntentBlocker? Evaluate(Goal goal, OperatorIntentRecord intent)
    {
        if (intent.TaskId is null || intent.Verb is not
            (OperatorIntentVerbs.Adjudicate or OperatorIntentVerbs.Progress or
             OperatorIntentVerbs.Retry or OperatorIntentVerbs.VerifyManual))
            return null;

        var state = GoalLifecycle.ResolveState(goal);
        if (state != GoalLifecycleState.Failed ||
            goal.Status is GoalStatus.Cancelled or GoalStatus.Superseded)
            return null;

        var failure = ConductorOutcomeText.LatestTaskFailure(goal, state);
        var blocker = failure?.TaskId is { } taskId
            ? goal.Tasks.First(task => task.Id == taskId)
            : goal.Tasks.FirstOrDefault(task => task.Status == WorkTaskStatus.Failed);
        return blocker is null ? null : Capture(goal, blocker);
    }

    private static OperatorIntentBlocker Capture(Goal goal, TaskSpec task)
    {
        var failures = goal.Timeline.Where(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskFailed &&
            (task.LatestRetryAt is null || item.OccurredAt >= task.LatestRetryAt)).ToArray();
        var failure = failures.LastOrDefault(item => item.Failure is not null);
        var reason = failure?.Failure?.Rule ?? failure?.Failure?.Kind ?? "failure rule not recorded";
        if (failure is not null && reason == TaskOutcomeRules.UnknownFailure.Token &&
            task.LastVerification is
                { EvidenceSource: VerificationEvidenceSource.Dispatch, ExitCode: not 0, WorkerResultPresent: false,
                  AuthoritativeStandardOutput: { } output } verification &&
            failure.Message.StartsWith($"Dispatch failed: rule={UnknownFailureExitPrefix}{verification.ExitCode}:", StringComparison.Ordinal) &&
            !WorkerResultBlockers.HasCompleteWorkerResult(output))
        {
            // Describe also runs from the persisted blocker, so retain the recorded exit evidence with the rule.
            reason = UnknownFailureExitPrefix + verification.ExitCode;
        }
        return new(task.Id.Value, task.RequiredRole, reason,
            goal.Tasks.ToList().IndexOf(task) + 1, failures.LastOrDefault()?.OccurredAt, task.LatestRetryAt);
    }

    internal static string Describe(OperatorIntentBlocker blocker) =>
        $"blocked by Failed {blocker.Role} task {blocker.TaskId} ({blocker.Reason}); " +
        $"close or route task {blocker.TaskNumber} to proceed" +
        (IsDispatchFailure(blocker.Reason) ? ", or run recover for this goal" : "");

    private static bool IsDispatchFailure(string reason)
    {
        var classification = TaskOutcomeClassifier.Classify(WorkTaskStatus.Failed, reason);
        return reason.StartsWith(UnknownFailureExitPrefix, StringComparison.Ordinal) ||
            classification.Class is TaskOutcomeClass.Environmental or TaskOutcomeClass.ManufacturedFixed ||
            classification.Rule is
                DispatchFailureDiagnosticMarker.ResearcherOutputContractRejected or
                DispatchFailureDiagnosticMarker.ResearcherArtifactPersistenceFailed or
                DispatchFailureDiagnosticMarker.PlannerOutputContractRejected or
                DispatchFailureDiagnosticMarker.PlannerPlanPersistenceFailed or
                DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed or
                DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing or
                DispatchFailureDiagnosticMarker.WorkerBuildReceiptStale or
                DispatchFailureDiagnosticMarker.WrapperProcessExitFailure or
                DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing or
                DispatchFailureDiagnosticMarker.InScopeFileIgnored or
                DispatchFailureDiagnosticMarker.WorktreeInspectionFailed ||
            classification.Rule == TaskOutcomeRules.VerificationTerminalContractIncomplete.Token;
    }

    internal static IEnumerable<OperatorIntentAppliedPayload> PendingBlocked(Goal goal)
    {
        foreach (var item in goal.Timeline)
        {
            if (item.OperatorIntentApplied is not { BlockedBy: { } blocker } applied)
                continue;
            var task = goal.Tasks.FirstOrDefault(task => task.Id.Value == blocker.TaskId);
            if (task is not { Status: WorkTaskStatus.Failed })
                continue;
            var current = Capture(goal, task);
            if (current == blocker ||
                (blocker.Reason == TaskOutcomeRules.UnknownFailure.Token &&
                 current.Reason.StartsWith(UnknownFailureExitPrefix, StringComparison.Ordinal) &&
                 (current with { Reason = blocker.Reason }) == blocker))
                yield return applied;
        }
    }
}
