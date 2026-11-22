using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

// An obligation is the durable, authoritative statement of who can close a
// criterion. It is deliberately separate from a worker's criterion verdict:
// a verdict is an observation; this record controls whether that observation
// is sufficient to move the goal.
public enum CriterionEvidenceOwner
{
    Worker,
    Acceptance,
    Operator,
    Unknown
}

public enum CriterionEvidenceState
{
    Pending,
    Failed,
    Satisfied,
    // A malformed, Unknown claim may only reach this terminal history state
    // through the attributed repair operation.  It is not evidence and it
    // always names the replacement obligation in Detail.
    Repaired,
    // An attributed retirement of a post-landing duty, without a receipt.
    Superseded
}

public static class CriterionEvidenceScopes
{
    // A successful normal acceptance run proves this exact, bounded scope. It
    // cannot be substituted for a manual/native observation or a narrower gate.
    public const string FullAcceptanceGate = "acceptance:full-gate";
}

public sealed record CriterionEvidenceObligation(
    string Id,
    int CriterionIndex,
    int CriterionVersion,
    string Criterion,
    CriterionEvidenceOwner Owner,
    CriterionEvidenceState State,
    string RequiredScope,
    string Provenance,
    DateTimeOffset RecordedAt,
    string? CandidateSha = null,
    string? ReceiptId = null,
    string? Detail = null,
    string? FindingStableId = null,
    string? ExpectedCandidateSha = null,
    IReadOnlyList<CriterionEvidenceReceipt>? PriorReceipts = null,
    string? ReplacementObligationId = null,
    CriterionEvidenceOwner? ReplacementOwner = null,
    CriterionEvidencePhase Phase = CriterionEvidencePhase.PreLanding,
    CriterionObservationScope? ObservationScope = null,
    GoalPromotionFact? Publication = null,
    CriterionPostLandingObservation? Observation = null,
    string? ObservationActor = null,
    IReadOnlyList<PlannerCriterionEvidenceSource>? PlannerSources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OperatorActorKind? ObservationActorKind = null)
{
    [JsonIgnore]
    public int CriterionNumber => CriterionIndex + 1;

    [JsonIgnore]
    public string DisplayLabel => DescribeCriterion(CriterionVersion, CriterionIndex);

    public static string DescribeCriterion(int criterionVersion, int criterionIndex) =>
        $"criterion {criterionIndex + 1} (v{criterionVersion}) {BuildId(criterionVersion, criterionIndex)}";

    public bool IsPending => State == CriterionEvidenceState.Pending;

    public bool IsValidatedPostLanding => Phase == CriterionEvidencePhase.PostLanding &&
        CriterionEvidenceLifecycle.IsValid(Phase, Owner, ObservationScope);

    [JsonIgnore]
    public bool IsOutstandingPostLandingDuty => IsValidatedPostLanding &&
        State is not (CriterionEvidenceState.Satisfied or CriterionEvidenceState.Repaired or CriterionEvidenceState.Superseded);

    public bool IsLandingPrerequisite => !(IsValidatedPostLanding &&
        State is CriterionEvidenceState.Pending or CriterionEvidenceState.Satisfied or CriterionEvidenceState.Superseded &&
        HasValidEvidenceState);

    internal bool HasValidEvidenceState =>
        CriterionEvidenceLifecycle.IsValid(Phase, Owner, ObservationScope) &&
        (Phase != CriterionEvidencePhase.PreLanding ||
         (Publication is null && Observation is null && ObservationActor is null && ObservationActorKind is null &&
          (PriorReceipts is null || PriorReceipts.All(receipt => receipt is { Observation: null })))) &&
        (Phase != CriterionEvidencePhase.PostLanding ||
         (State == CriterionEvidenceState.Pending ||
          (State == CriterionEvidenceState.Superseded && Observation is null) ||
          (Observation is not null && !string.IsNullOrWhiteSpace(ObservationActor) &&
           CriterionEvidenceLifecycle.MatchesObservation(this, Observation, RecordedAt)))) &&
        (State != CriterionEvidenceState.Pending || Observation is null && ObservationActor is null && ObservationActorKind is null) &&
        (Phase != CriterionEvidencePhase.PostLanding || PriorReceipts is null ||
         PriorReceipts.All(receipt => receipt is not null && receipt.Owner == CriterionEvidenceOwner.Operator &&
             receipt.Observation is not null && !string.IsNullOrWhiteSpace(receipt.Actor) &&
             string.Equals(receipt.CandidateSha, Publication?.CandidateRevision, StringComparison.OrdinalIgnoreCase) &&
             CriterionEvidenceLifecycle.MatchesObservation(this, receipt.Observation, receipt.RecordedAt))) &&
        Owner is CriterionEvidenceOwner.Operator or CriterionEvidenceOwner.Acceptance or CriterionEvidenceOwner.Unknown &&
        State is CriterionEvidenceState.Pending or CriterionEvidenceState.Failed or CriterionEvidenceState.Satisfied or
            CriterionEvidenceState.Repaired or CriterionEvidenceState.Superseded &&
        (Owner != CriterionEvidenceOwner.Unknown || State is CriterionEvidenceState.Pending or CriterionEvidenceState.Repaired) &&
        (Owner != CriterionEvidenceOwner.Acceptance ||
         (RequiredScope == CriterionEvidenceScopes.FullAcceptanceGate &&
          (State == CriterionEvidenceState.Pending || !string.IsNullOrWhiteSpace(ExpectedCandidateSha)))) &&
        HasValidCurrentRecord &&
        (PriorReceipts is null ||
         (PriorReceipts.All(receipt => receipt is { IsWellFormed: true } && receipt.ReceiptId != ReceiptId) &&
          PriorReceipts.Select(receipt => receipt.ReceiptId).Distinct(StringComparer.Ordinal).Count() == PriorReceipts.Count));

    private bool HasValidCurrentRecord => State switch
    {
        CriterionEvidenceState.Pending or CriterionEvidenceState.Repaired =>
            CandidateSha is null && ReceiptId is null && Detail is null,
        CriterionEvidenceState.Superseded =>
            Owner == CriterionEvidenceOwner.Operator && Phase == CriterionEvidencePhase.PostLanding &&
            CandidateSha is null && ReceiptId is null && Observation is null &&
            !string.IsNullOrWhiteSpace(Detail) && !string.IsNullOrWhiteSpace(ObservationActor),
        _ => CurrentReceipt() is { IsWellFormed: true } &&
             (string.IsNullOrWhiteSpace(ExpectedCandidateSha) ||
              string.Equals(ExpectedCandidateSha, CandidateSha, StringComparison.OrdinalIgnoreCase))
    };

    public bool HasSatisfiedEvidenceFor(string? candidateSha) =>
        State == CriterionEvidenceState.Satisfied && HasValidEvidenceState &&
        !string.IsNullOrWhiteSpace(candidateSha) &&
        string.Equals(CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(ExpectedCandidateSha) ||
         string.Equals(ExpectedCandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase));

    internal CriterionEvidenceReceipt? FindReceipt(string receiptId) =>
        CurrentReceipt() is { } current && current.ReceiptId == receiptId
            ? current
            : PriorReceipts?.SingleOrDefault(receipt => receipt.ReceiptId == receiptId);

    internal IReadOnlyList<CriterionEvidenceReceipt>? ArchiveCurrentReceipt() =>
        CurrentReceipt() is { } current ? [.. PriorReceipts ?? [], current] : PriorReceipts;

    private CriterionEvidenceReceipt? CurrentReceipt() =>
        State is CriterionEvidenceState.Failed or CriterionEvidenceState.Satisfied && CandidateSha is not null && ReceiptId is not null && Detail is not null
            ? new(Owner, CandidateSha, ReceiptId, RequiredScope, State == CriterionEvidenceState.Satisfied,
                Detail, Provenance, RecordedAt, Observation, ObservationActor, ObservationActorKind)
            : null;

    public static string BuildId(int criterionVersion, int criterionIndex) =>
        $"criterion-v{criterionVersion}-{criterionIndex}";
}
