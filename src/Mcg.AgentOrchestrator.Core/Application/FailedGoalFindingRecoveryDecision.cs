namespace Mcg.AgentOrchestrator.Core;

internal static class FailedGoalFindingRecoveryDecision
{
    internal static FailedGoalRecoveryDecision Decide(
        FailedGoalRecoveryFacts facts,
        FailedGoalFindingObservation observation,
        string defaultAttribution)
    {
        var (action, cause, roundKind) = observation.Kind switch
        {
            FailedGoalFindingObservationKind.FindingEvidencePending or
            FailedGoalFindingObservationKind.FindingStagnationDecisionRecorded =>
                (FailedGoalRecoveryAction.Hold, (RetryCause?)null, (RetryRoundKind?)null),
            FailedGoalFindingObservationKind.ReviewContractRepairEnvelopeAvailable =>
                (FailedGoalRecoveryAction.FindingRetry, RetryCause.CriterionEvidenceOwnerMismatch, RetryRoundKind.Mechanical),
            FailedGoalFindingObservationKind.FindingEvidenceDeliveryRecorded =>
                (FailedGoalRecoveryAction.FindingRetry, RetryCause.CriterionEvidenceOwnerMismatch, RetryRoundKind.Mechanical),
            FailedGoalFindingObservationKind.FindingResultMissing =>
                (FailedGoalRecoveryAction.FindingRetry, RetryCause.EnvironmentApparatusFailure, RetryRoundKind.Mechanical),
            FailedGoalFindingObservationKind.FindingActionableRed =>
                (FailedGoalRecoveryAction.FindingRetry, RetryCause.NewSourceFinding, (RetryRoundKind?)null),
            FailedGoalFindingObservationKind.FindingRouteObserved =>
                (FailedGoalRecoveryAction.FindingRetry, observation.ObservedCause, observation.RoundKind),
            FailedGoalFindingObservationKind.ReviewContractLedgerUnavailable or
            FailedGoalFindingObservationKind.ReviewRetryCapReached or
            FailedGoalFindingObservationKind.ReviewTouchProofUnavailable or
            FailedGoalFindingObservationKind.ReviewContractRepairLimitReached or
            FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired or
            FailedGoalFindingObservationKind.FindingRouteUnavailable or
            FailedGoalFindingObservationKind.FindingRetryCapReached or
            FailedGoalFindingObservationKind.FindingRepeatedFailingTestSet or
            FailedGoalFindingObservationKind.FindingConvergenceViolation or
            FailedGoalFindingObservationKind.FindingEvidenceReceiptPersistenceFailed or
            FailedGoalFindingObservationKind.FindingActionableRedRouteUnavailable =>
                (FailedGoalRecoveryAction.Escalate, (RetryCause?)null, (RetryRoundKind?)null),
            FailedGoalFindingObservationKind.None => throw new InvalidOperationException(
                "An empty finding observation cannot select a recovery action."),
            _ => throw new InvalidOperationException($"Unknown finding observation {observation.Kind}.")
        };
        if (action == FailedGoalRecoveryAction.FindingRetry &&
            (observation.TargetTaskId is null ||
             string.IsNullOrWhiteSpace(observation.AttemptIdentity) ||
             cause is null))
        {
            throw new InvalidOperationException(
                "A finding retry observation must carry target attempt identity and a typed retry cause.");
        }

        return new FailedGoalRecoveryDecision(
            action,
            new FailedGoalRecoveryIdentity(
                facts.GoalId,
                observation.TargetTaskId,
                observation.AttemptIdentity,
                facts.ContextVersion),
            8,
            string.IsNullOrWhiteSpace(observation.Attribution) ? defaultAttribution : observation.Attribution,
            observation.Evidence,
            cause,
            roundKind,
            WarningMessage: observation.WarningMessage, HoldOwner: observation.HoldOwner);
    }
}
