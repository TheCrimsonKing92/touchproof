namespace Mcg.AgentOrchestrator.Core;

// An explicit, attributed operator decision changes when one pending duty is due.
public static class CriterionEvidenceRephase
{
    public const string ProvenanceMarker = "re-phased to PostLanding by ";

    public static CriterionEvidenceObligation Apply(CriterionEvidenceObligation obligation,
        CriterionObservationScope? scope, string actor, string reason, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        if (obligation.State != CriterionEvidenceState.Pending)
            throw new InvalidOperationException($"Criterion evidence obligation '{obligation.Id}' must be Pending; state is {obligation.State}.");
        if (obligation.Owner != CriterionEvidenceOwner.Operator)
            throw new InvalidOperationException($"Criterion evidence obligation '{obligation.Id}' must be Operator-owned; owner is {obligation.Owner}.");
        if (obligation.Phase != CriterionEvidencePhase.PreLanding)
            throw new InvalidOperationException($"Criterion evidence obligation '{obligation.Id}' must be PreLanding; phase is {obligation.Phase}.");
        if (scope is not { IsValid: true })
            throw new ArgumentException($"Criterion evidence obligation '{obligation.Id}' requires a valid observation scope.", nameof(scope));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException($"Criterion evidence obligation '{obligation.Id}' requires a re-phase reason.", nameof(reason));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException($"Criterion evidence obligation '{obligation.Id}' requires a re-phase actor.", nameof(actor));

        var result = obligation with
        {
            Phase = CriterionEvidencePhase.PostLanding,
            ObservationScope = scope,
            Provenance = $"{obligation.Provenance}; {ProvenanceMarker}{NormalizeSingleLine(actor)} at {recordedAt:u}; reason={NormalizeSingleLine(reason)}"
        };
        if (!result.HasValidEvidenceState)
            throw new InvalidOperationException($"Criterion evidence obligation '{obligation.Id}' cannot retain evidence or prior receipts without valid post-landing observations.");
        return result;
    }

    internal static bool IsRecordedRephase(CriterionEvidenceObligation obligation,
        CriterionEvidenceDeclaration? declaration) =>
        obligation.IsValidatedPostLanding &&
        (declaration is null || declaration.Phase == CriterionEvidencePhase.PreLanding) &&
        obligation.Provenance?.Contains(ProvenanceMarker, StringComparison.Ordinal) == true;

    private static string NormalizeSingleLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
