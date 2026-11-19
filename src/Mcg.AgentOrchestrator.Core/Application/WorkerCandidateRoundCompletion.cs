namespace Mcg.AgentOrchestrator.Core;

internal static class WorkerCandidateRoundCompletion
{
    internal static bool Record(Goal goal, TaskSpec task, TaskVerificationRecord verification,
        string? outcomeRule, bool verifiedSuccess, Action<TaskId?, string> recordNote)
    {
        if (string.Equals(outcomeRule, TaskOutcomeRules.DeferredNoChangeRound.Token, StringComparison.Ordinal))
            RecordDeferredNoChangeOutcome(task, verification, recordNote);

        var isVerifiedNoChangeRound = string.Equals(
            outcomeRule, TaskOutcomeRules.VerifiedNoChangeRound.Token, StringComparison.Ordinal);
        if (isVerifiedNoChangeRound && task.LastDispatch is { } dispatch &&
            !string.IsNullOrWhiteSpace(dispatch.BaseCommit) && string.IsNullOrWhiteSpace(dispatch.ResultCommit))
        {
            task.SetDispatchResultCommit(dispatch.BaseCommit);
            recordNote(null,
                $"NO_CHANGE_DISPOSITION task={task.Id.Value} rule={TaskOutcomeRules.VerifiedNoChangeRound.Token} candidate={dispatch.BaseCommit}");
        }

        if (verifiedSuccess && task.RequiredRole is AgentRole.Developer or AgentRole.Tester &&
            task.LastDispatch?.ResultCommit is { } candidate && !string.IsNullOrWhiteSpace(candidate) &&
            goal.BindOperatorEvidenceToWorkerCandidate(candidate, verification.CompletedAt))
        {
            recordNote(task.Id, $"Bound outstanding Operator PreLanding evidence to candidate {candidate}.");
        }

        return isVerifiedNoChangeRound;
    }

    private static void RecordDeferredNoChangeOutcome(
        TaskSpec task, TaskVerificationRecord verification, Action<TaskId?, string> recordNote)
    {
        if (task.LastDispatch is not { BaseCommit: { Length: > 0 } candidate } ||
            !DeferredNoChangeOutcome.TryParse(verification.StandardError, out var marker) ||
            !string.Equals(marker.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Deferred no-change completion lost its candidate-bound outcome.");

        task.SetDispatchResultCommit(candidate);
        recordNote(task.Id,
            $"DEFERRED_NO_CHANGE_OUTCOME task={task.Id.Value} candidate_sha={candidate} " +
            $"classes={string.Join(',', marker.TestClasses)} rationale={marker.Rationale}");
    }
}
