using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Selects evidence holds before any paid retry; lifecycle authority and effects remain with the driver.
internal static class WorkerBuildEvidenceRecoveryHold
{
    internal static ConductorAdvanceOutcome.Held? Decide(
        Goal goal, GoalLifecycleState state, FailedGoalRecoveryIdentity identity)
    {
        var task = goal.Tasks.FirstOrDefault(candidate => candidate.Status == WorkTaskStatus.Failed &&
            candidate.RequiredRole is AgentRole.Developer or AgentRole.Tester &&
            candidate.LastVerification is { } verification &&
            (verification.OrchestratorFailureReason is "completion-build-coordinator-unavailable" or "worker-build-authority-rejected" ||
             DispatchFailureDiagnosticMarker.SharesMissingEvidenceRecovery(TaskOutcomeClassifier.TryExtractRule(
                 DispatchFailureClassifier.Classify(candidate, verification).ClassifierReceipt)) &&
             candidate.CompletionBuildEvidence is { IsPending: false, Phase: not CompletionBuildEvidencePhase.CompilerFailure } &&
             BackgroundDispatchRunner.HasCurrentCompletionBuildWorker(candidate)));
        if (task is null) return UnverifiedChannelHold(goal, state, identity);
        var operationState = task.CompletionBuildEvidence is { } build
            ? $"operation={build.Id}; outcome={build.Phase}; automatic attempt exhausted"
            : task.LastVerification!.OrchestratorFailureReason == "worker-build-authority-rejected"
                ? "host build authority rejected; inspect task verification before restoring authority"
                : "existing completion-build coordinator unavailable";
        var reason = $"Completion build evidence hold: task={task.Id.Value}; {operationState}. " +
            "Candidate edits are retained; genuine successful current build evidence with exact project coverage is required. " +
            "An operator decision is required before further build execution; no paid coding retry is authorized solely for missing evidence. " +
            $"Next action: the operator runs {OrchestratorBuildEvidenceCheck.ScriptRelativePath} on the goal worktree for this candidate, " +
            $"then closes task {task.Id.Value} with operator evidence. " + ConductorDriver.BuildAdjudicateRemedy(goal.Status);
        return new ConductorAdvanceOutcome.Held(state, reason) {
            Owner = ConductorHoldOwner.Operator,
            Decision = FailedGoalRecoveryDecisionRecords.CompletionBuildEvidenceHeld(identity, reason) };
    }

    private static ConductorAdvanceOutcome.Held? UnverifiedChannelHold(
        Goal goal, GoalLifecycleState state, FailedGoalRecoveryIdentity identity)
    {
        var task = goal.Tasks.FirstOrDefault(candidate =>
            candidate.Status == WorkTaskStatus.Failed &&
            candidate.RequiredRole is AgentRole.Developer or AgentRole.Tester &&
            candidate.LastVerification is { } verification &&
            DispatchFailureDiagnosticMarker.SharesMissingEvidenceRecovery(TaskOutcomeClassifier.TryExtractRule(
                DispatchFailureClassifier.Classify(candidate, verification).ClassifierReceipt)) &&
            (verification.StandardOutput + Environment.NewLine + verification.StandardError)
                .Contains(OrchestratorBuildEvidenceCheck.BusyChannelClaim, StringComparison.Ordinal) &&
            !candidate.WorkerBuildInvocations.Any(invocation => invocation.DispatchAt == candidate.LastDispatch?.DispatchedAt) &&
            !(candidate.CompletionBuildEvidence is { } operation && operation.DispatchAt == candidate.LastDispatch?.DispatchedAt));
        if (task is null) return null;
        var reason = $"Worker build channel evidence hold: task={task.Id.Value}; dispatch={task.LastDispatch?.DispatchedAt:O}; " +
            "busy exhaustion is unverified; build evidence remains missing and candidate edits are retained. " +
            "Provide a trusted host-captured generated-client terminal result bound to this dispatch, channel and completion, " +
            "or an operator decision authorizing same-candidate build evidence through the existing completion workflow. " +
            "No automatic build or paid Developer correction is authorized by this claim.";
        return new ConductorAdvanceOutcome.Held(state, reason) {
            Decision = FailedGoalRecoveryDecisionRecords.BuildChannelEvidenceHeld(identity, reason) };
    }
}
