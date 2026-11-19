namespace Mcg.AgentOrchestrator.Core;

internal static class WorkerCandidateOperatorEvidenceBinding
{
    internal static bool CanFollowWorkerCandidate(CriterionEvidenceObligation obligation) =>
        obligation.Owner == CriterionEvidenceOwner.Operator && obligation.Phase == CriterionEvidencePhase.PreLanding &&
        obligation.State is CriterionEvidenceState.Pending or CriterionEvidenceState.Failed &&
        !obligation.Provenance.StartsWith("operator mapping by ", StringComparison.Ordinal);

    internal static CriterionEvidenceObligation Apply(
        CriterionEvidenceObligation obligation, string candidateSha, DateTimeOffset recordedAt)
    {
        if (!CanFollowWorkerCandidate(obligation) ||
            string.Equals(obligation.ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
            return obligation;

        if (obligation.State == CriterionEvidenceState.Failed &&
            !string.Equals(obligation.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
        {
            obligation = obligation with
            {
                State = CriterionEvidenceState.Pending,
                CandidateSha = null,
                ReceiptId = null,
                Detail = null,
                PriorReceipts = obligation.ArchiveCurrentReceipt()
            };
        }

        return obligation with { ExpectedCandidateSha = candidateSha, RecordedAt = recordedAt };
    }
}
