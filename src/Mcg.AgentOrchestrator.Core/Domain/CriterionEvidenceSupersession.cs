namespace Mcg.AgentOrchestrator.Core;

internal static class CriterionEvidenceSupersession
{
    internal static void ValidateRequest(CriterionEvidenceObligation obligation, string actor, string reason)
    {
        if (obligation.State != CriterionEvidenceState.Pending)
            throw new InvalidOperationException($"Criterion evidence obligation '{obligation.Id}' must be Pending; state is {obligation.State}.");
        if (obligation.Owner != CriterionEvidenceOwner.Operator)
            throw new InvalidOperationException($"Criterion evidence obligation '{obligation.Id}' must be Operator-owned; owner is {obligation.Owner}.");
        if (obligation.Phase != CriterionEvidencePhase.PostLanding)
            throw new InvalidOperationException($"Criterion evidence obligation '{obligation.Id}' must be PostLanding; phase is {obligation.Phase}.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A nonempty supersede reason is required.", nameof(reason));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("A supersede actor is required.", nameof(actor));
    }

    internal static CriterionEvidenceObligation Apply(GoalStatus status, GoalLifecycleState? lifecycleState, CriterionEvidenceObligation obligation,
        string actor, string reason, DateTimeOffset recordedAt)
    {
        ValidateRequest(obligation, actor, reason);
        if (status != GoalStatus.Completed && !(status == GoalStatus.Verified && lifecycleState == GoalLifecycleState.CleanedUp))
            throw new InvalidOperationException($"Goal must be Completed or CleanedUp; status is {status}.");

        var result = obligation with
        {
            State = CriterionEvidenceState.Superseded,
            Detail = reason.Trim(),
            ObservationActor = actor,
            RecordedAt = recordedAt
        };
        if (!result.HasValidEvidenceState)
            throw new InvalidOperationException($"Criterion evidence obligation '{obligation.Id}' must have a valid PostLanding declaration and evidence history.");
        return result;
    }
}
