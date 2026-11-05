namespace Mcg.AgentOrchestrator.Core;

// Owns request registration and deduplication. The kernel supplies lifecycle mutations and evidence sinks.
internal sealed class HumanInputRequestRegistrar(
    object requestLock, IDictionary<HumanInputRequestId, HumanInputRequest> requests, IClock clock,
    Action<Goal> ensureCanResume,
    Action<Goal, TaskId?, HumanWaitKind> holdTask,
    Action<GoalId, TaskId?, HumanInputRequestId?> resetStreaks,
    Action<Goal, TaskId?, HumanInputRequest, bool, int?, string?> appendDuplicate,
    Action<Goal, TaskId?, HumanInputRequest, int?, string?> restoreSuppressed,
    Action<Goal, HumanInputRequest> appendAdvisory,
    Action<Goal, TaskId?, ProgressKind, string> append)
{
    internal HumanInputRequestCreationResult Request(
        Goal goal,
        TaskId? taskId,
        string question,
        HumanWaitKind kind = HumanWaitKind.SpecClarification,
        bool? isAutoDefaultable = null,
        bool? isDismissible = null,
        bool isAnswerRequired = true,
        bool? isExternallyBlocked = null,
        string? suggestedDefaultAnswer = null,
        string? resumeCommand = null,
        string? questionFingerprint = null,
        string? blockerFingerprint = null,
        int? completedRound = null,
        string? workerResultLogReference = null,
        bool recordDuplicateSuppression = true,
        string? evidenceOwner = null,
        PlannerEvidenceStoreReference? storeReference = null,
        IReadOnlyList<string>? testNames = null, PlannerEvidenceIdentity? plannerIdentity = null, PlannerEvidencePayload? plannerEvidence = null,
        int? amendmentCriterionIndex = null)
    {
        var goalId = goal.Id;
        if (HumanWaitPolicyDefaults.BlocksActiveWork(kind))
            ensureCanResume(goal);
        if (taskId is not null)
        {
            goal.FindTask(taskId);
        }
        if (kind == HumanWaitKind.PlannerBriefAmendment &&
            (amendmentCriterionIndex is not > 0 || taskId is null || goal.FindTask(taskId).RequiredRole != AgentRole.Planner))
            throw new ArgumentException("Planner brief amendment requires a Planner task and positive criterion index.");

        var effectiveQuestionFingerprint = string.IsNullOrWhiteSpace(questionFingerprint)
            ? HumanInputRequest.BuildQuestionFingerprint(question)
            : questionFingerprint.Trim();

        lock (requestLock)
        {
            var matches = requests.Values
                .Where(candidate =>
                    candidate.GoalId == goalId &&
                    candidate.TaskId == taskId &&
                    candidate.Kind == kind &&
                    (kind != HumanWaitKind.PlannerBriefAmendment || !candidate.AmendmentReplanRouted &&
                        (goal.FindTask(taskId!).LatestRetryAt is not { } retryAt || candidate.RequestedAt >= retryAt)) &&
                    (plannerIdentity is null && candidate.PlannerIdentity is null
                        ? string.Equals(candidate.QuestionFingerprint, effectiveQuestionFingerprint, StringComparison.Ordinal)
                        : plannerIdentity is not null && candidate.PlannerIdentity == plannerIdentity))
                .OrderBy(candidate => candidate.RequestedAt)
                .ThenBy(candidate => candidate.Id.Value, StringComparer.Ordinal)
                .ToList();
            if (plannerIdentity is not null && matches.Any(candidate => !plannerEvidence!.SemanticallyEquals(candidate.PlannerEvidence)))
                throw new InvalidOperationException("Conflicting stored Planner evidence payload; request a new brief before changing the requirement.");
            var open = matches.FirstOrDefault(candidate => !candidate.IsCompleted);
            if (open is not null)
            {
                if (recordDuplicateSuppression && completedRound is not null)
                {
                    resetStreaks(goalId, taskId, null);
                }

                holdTask(goal, taskId, open.Kind);
                if (recordDuplicateSuppression)
                {
                    open.IncrementSuppressionCount();
                    appendDuplicate(
                        goal,
                        taskId,
                        open,
                        false,
                        completedRound,
                        workerResultLogReference);
                }

                return new HumanInputRequestCreationResult(open, WasReused: true, WasSuppressedByAnswer: false);
            }

            if (!string.IsNullOrWhiteSpace(blockerFingerprint))
            {
                var answered = matches.FirstOrDefault(candidate =>
                    candidate.IsCompleted &&
                    !candidate.WasDismissed &&
                    !candidate.IsSyntheticParkedHumanWaitCompletion &&
                    !string.IsNullOrWhiteSpace(candidate.Answer) &&
                    string.Equals(candidate.BlockerFingerprint, blockerFingerprint, StringComparison.Ordinal));
                if (answered is not null)
                {
                    if (!recordDuplicateSuppression)
                    {
                        return new HumanInputRequestCreationResult(answered, WasReused: false, WasSuppressedByAnswer: true);
                    }

                    if (completedRound is not null)
                    {
                        resetStreaks(goalId, taskId, answered.Id);
                    }
                    answered.IncrementSuppressionCount();
                    restoreSuppressed(
                        goal,
                        taskId,
                        answered,
                        completedRound,
                        workerResultLogReference);
                    appendDuplicate(
                        goal,
                        taskId,
                        answered,
                        true,
                        completedRound,
                        workerResultLogReference);
                    return new HumanInputRequestCreationResult(answered, WasReused: false, WasSuppressedByAnswer: true);
                }
            }

            if (completedRound is not null)
            {
                resetStreaks(goalId, taskId, null);
            }
            var id = HumanInputRequestId.New();
            var request = new HumanInputRequest(
                id,
                goal.Id,
                taskId,
                question,
                clock.UtcNow,
                kind,
                isAutoDefaultable,
                isDismissible,
                isAnswerRequired,
                isExternallyBlocked,
                suggestedDefaultAnswer,
                resumeCommand ?? HumanInputRequest.BuildDefaultResumeCommand(id),
                effectiveQuestionFingerprint,
                blockerFingerprint,
                evidenceOwner: evidenceOwner,
                storeReference: storeReference,
                testNames: testNames, plannerIdentity: plannerIdentity, plannerEvidence: plannerEvidence,
                amendment: kind == HumanWaitKind.PlannerBriefAmendment
                    ? new PlannerBriefAmendmentPayload(amendmentCriterionIndex!.Value, goal.AuthoritativeBrief.Version) : null);
            requests.Add(request.Id, request);

            holdTask(goal, taskId, request.Kind);
            append(goal, taskId, ProgressKind.HumanInputRequested, question);
            appendAdvisory(goal, request);
            return new HumanInputRequestCreationResult(request, WasReused: false, WasSuppressedByAnswer: false);
        }
    }

}
