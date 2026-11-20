namespace Mcg.AgentOrchestrator.Core;

public enum HumanInputAnswerEffect
{
    NoTaskEffect,
    CompletesTask,
    ReturnsTaskForAnotherRound,
    TaskStatus
}

public sealed record HumanInputAnswerOutcome(HumanInputAnswerEffect Effect, WorkTaskStatus? ResultingStatus);

internal static class HumanInputAnswerOutcomePredictor
{
    internal static HumanInputAnswerOutcome Predict(Goal goal, TaskSpec? task, HumanInputRequest request,
        IEnumerable<HumanInputRequest> requests, DateTimeOffset answeredAt,
        Func<Goal, TaskSpec, HumanInputRequest, string, bool> shouldRequeueTester,
        Func<TaskSpec, RetryAdmissionReceipt?> getRefusedDispatch)
    {
        if (task is null || !HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind))
            return new(HumanInputAnswerEffect.NoTaskEffect, null);

        // Routing needs an ordinary answer on the selected request, including for
        // amendments. Only the detached copies receive answers.
        const string plainAnswer = "Answer";
        var projectedRequests = requests
            .Where(item => item.GoalId == goal.Id && item.TaskId == task.Id)
            .Select(item => item.IsCompleted ? item : HumanInputRequest.FromSnapshot(item.ToSnapshot()))
            .ToArray();
        var answered = projectedRequests.Single(item => item.Id == request.Id);
        var briefVersion = goal.AuthoritativeBrief.Version;
        answered.Complete(plainAnswer, answeredAt, briefVersion: briefVersion);
        HumanInputSubmission.CompleteSiblings(projectedRequests, answered, plainAnswer, answeredAt,
            briefVersion, supersedeSiblings: true, isAutoDefaulted: false);

        if (HumanInputAnswerRouting.HasOpenBlockingRequest(goal, task, projectedRequests))
            return new(HumanInputAnswerEffect.TaskStatus, task.Status);
        if (HumanInputAnswerRouting.RetainsRejectedPlannerFailure(task, request) ||
            HumanInputAnswerRouting.FailsHistoricalPlannerAnswer(goal, task, request))
            return new(HumanInputAnswerEffect.TaskStatus, WorkTaskStatus.Failed);
        if (shouldRequeueTester(goal, task, request, plainAnswer))
            return new(HumanInputAnswerEffect.ReturnsTaskForAnotherRound, HumanInputAnswerRouting.GetRetryResetStatus(task));

        if (PlannerBriefAmendmentRouting.Evaluate(goal, task, projectedRequests) is { } amendment)
            return amendment.Replan
                ? new(HumanInputAnswerEffect.ReturnsTaskForAnotherRound, HumanInputAnswerRouting.GetRetryResetStatus(task))
                : new(HumanInputAnswerEffect.TaskStatus, task.Status);
        if (PlannerRoundRecovery.GetAnswerRequeueNote(goal, task, out var premiseInvalidPlanner) is not null)
            return new(HumanInputAnswerEffect.ReturnsTaskForAnotherRound, WorkTaskStatus.Assigned);

        if (!premiseInvalidPlanner &&
            PassingVerificationCompletion.IsEligible(goal, task, projectedRequests, out var verification) &&
            CanProjectPlannerCompletion(goal, task, verification))
            return new(HumanInputAnswerEffect.CompletesTask, WorkTaskStatus.Completed);

        return new(HumanInputAnswerEffect.ReturnsTaskForAnotherRound,
            HumanInputAnswerRouting.GetRestoreStatus(task, getRefusedDispatch(task)));
    }

    private static bool CanProjectPlannerCompletion(Goal goal, TaskSpec task, TaskVerificationRecord verification)
    {
        if (PassingVerificationCompletion.GetPlannerEvidenceBatch(task, verification) is not { } batch) return true;
        if (PlannerEvidenceCompletion.ValidateProvenance(task, verification, out var briefVersion) is not null)
            return false;
        return goal.LifecycleStatus is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded ||
            PlannerEvidenceRecorder.PlanOwnership(goal, task, batch, briefVersion, verification.CompletedAt, out _) is null;
    }
}

internal static class HumanInputAnswerRouting
{
    internal static bool HasOpenBlockingRequest(Goal goal, TaskSpec task, IEnumerable<HumanInputRequest> requests) =>
        requests.Any(candidate =>
            candidate.GoalId == goal.Id &&
            candidate.TaskId == task.Id &&
            !candidate.IsCompleted &&
            candidate.IsCurrentForBrief(goal.AuthoritativeBrief.Version) &&
            HumanWaitPolicyDefaults.BlocksActiveWork(candidate.Kind));

    internal static bool RetainsRejectedPlannerFailure(TaskSpec task, HumanInputRequest request) =>
        request.PlannerIdentity is not null && task.RequiredRole == AgentRole.Planner &&
        task.Status == WorkTaskStatus.Failed && task.LastVerification is { Succeeded: false };

    internal static bool FailsHistoricalPlannerAnswer(Goal goal, TaskSpec task, HumanInputRequest request) =>
        request.PlannerIdentity is { } identity &&
        identity.BriefVersion != goal.AuthoritativeBrief.Version &&
        task.RequiredRole == AgentRole.Planner &&
        task.Status is WorkTaskStatus.WaitingForHuman or WorkTaskStatus.Failed &&
        (task.LastDispatch is null || task.LastDispatch.BriefVersion != goal.AuthoritativeBrief.Version) &&
        task.LastProcess is not { IsRunning: true };

    internal static WorkTaskStatus GetRetryResetStatus(TaskSpec task) =>
        task.AssignedAgentId is null ? WorkTaskStatus.Pending : WorkTaskStatus.Assigned;

    internal static WorkTaskStatus GetRestoreStatus(TaskSpec task, RetryAdmissionReceipt? refusedDispatch) =>
        task.LastProcess is { IsRunning: true } ||
        (task.LastDispatch is not null && task.LastProcess is null && refusedDispatch is null)
            ? WorkTaskStatus.Running
            : WorkTaskStatus.Assigned;
}

internal static class PassingVerificationCompletion
{
    internal static bool IsEligible(
        Goal goal, TaskSpec task, IEnumerable<HumanInputRequest> requests,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TaskVerificationRecord? verification)
    {
        verification = task.LastVerification;
        if (task.RequiredRole == AgentRole.Planner && verification?.PlannerEvidenceBatch?.Directives
                .Any(directive => directive.Kind == HumanWaitKind.PlannerBriefAmendment) == true) return false;
        if (PlannerEvidenceRecorder.IsObsoletePlannerEvidenceRound(goal, task)) return false;
        if (task.Status == WorkTaskStatus.Completed ||
            verification is not { Succeeded: true }) return false;

        var terminalContract = WorkerResultBlockers.AssessSubscriptionVerification(task, verification);
        if (!terminalContract.AllowsCompletion ||
            (terminalContract.Status == VerificationTerminalContractStatus.Valid &&
             DispatchFailureClassifier.Classify(task, verification).Kind != DispatchOutcomeKind.VerifiedSuccess &&
             !VerificationFindingEvidencePolicy.HasOnlyAuthoritativelyDeferredOffWorkerFindings(goal, task, verification)) ||
            (task.RequiredRole == AgentRole.Developer &&
             WorkerResultBlockers.GetAssignedScopeComplete(verification) is false)) return false;

        return !HumanInputAnswerRouting.HasOpenBlockingRequest(goal, task, requests);
    }

    internal static HumanInputDirectiveParseResult? GetPlannerEvidenceBatch(
        TaskSpec task, TaskVerificationRecord verification) =>
        task.RequiredRole == AgentRole.Planner && verification.PlannerEvidenceBatch is { } batch &&
        batch.Directives.Any(item => item.PlannerEvidence is not null) ? batch : null;
}
