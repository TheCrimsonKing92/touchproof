namespace Mcg.AgentOrchestrator.Core;

public static class FindingStagnationDecisionRecorder
{
    public static void RecordSources(AgentOrchestratorKernel kernel, Goal goal, TaskSpec triggeringTask,
        IReadOnlyList<ReviewFinding> routedFindings)
    {
        var roundKey = FindingStagnationTracker.GetRoundKey(goal, triggeringTask.LastVerification!.CompletedAt);
        foreach (var stableId in routedFindings.Select(finding => finding.StableId).Distinct(StringComparer.Ordinal))
        {
            var source = new FindingStagnationRouteSource(stableId, roundKey, triggeringTask.RequiredRole,
                FindingStagnationTracker.GetResetAt(goal, kernel.HumanInputRequests, stableId));
            var message = source.ToTimelineMessage();
            if (!goal.Timeline.Any(evt => evt.Kind == ProgressKind.TaskNote && evt.Message == message))
                kernel.RecordTaskNote(goal.Id, triggeringTask.Id, message);
        }
    }

    public static HumanInputRequestCreationResult Record(AgentOrchestratorKernel kernel, FindingStagnationDecision decision) =>
        // The decision belongs to the goal/finding, even if both verifying roles route it.
        kernel.RequestHumanInputDeduplicated(decision.GoalId, null, decision.Question,
            HumanWaitKind.RecoveryChoice, isAutoDefaultable: false, isDismissible: false,
            questionFingerprint: decision.QuestionFingerprint, blockerFingerprint: decision.QuestionFingerprint,
            recordDuplicateSuppression: false);

    /// <summary>Operator resets supersede the wait while retaining the original decision as history.</summary>
    public static void ReconcileResets(AgentOrchestratorKernel kernel, Goal goal)
    {
        foreach (var request in kernel.HumanInputRequests.Where(request => request.GoalId == goal.Id &&
                !request.IsCompleted && request.Kind == HumanWaitKind.RecoveryChoice &&
                FindingStagnationDecision.ReadStableId(goal.Id, request.QuestionFingerprint) is { } stableId &&
                request.RequestedAt <= FindingStagnationTracker.GetResetAt(goal, kernel.HumanInputRequests, stableId)).ToArray())
            kernel.SubmitHumanInput(request.Id, "Finding stagnation wait superseded by the recorded operator criterion change or ruling.",
                origin: HumanInputAnswerOrigin.Worker, supersedeSiblings: false);
    }
}
