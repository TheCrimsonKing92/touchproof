namespace Mcg.AgentOrchestrator.App.Orchestration;

// Public refusal heads at the solo correction boundary; details belong after a colon.
internal static class SoloHoldReasons
{
    internal const string AwaitingConsumption = "solo-evidence-awaiting-consumption";
    internal const string RetryCap = "solo-acceptance-retry-cap";
    internal const string CorrectionCeiling = "solo-acceptance-correction-ceiling";
    internal const string ReceiptAbsent = "solo-receipt-absent";
    internal const string ReceiptAbsentInherited = "solo-receipt-absent-inherited";
    internal const string RevisionMismatch = "solo-revision-mismatch";
    internal const string FreshGateUnavailable = "solo-fresh-gate-unavailable";
    internal const string CurrentIdentityUnavailable = "solo-current-identity-unavailable";
    internal const string CaptureUnavailable = "solo-evidence-capture-unavailable";
    internal const string GoalOrAttemptMismatch = "solo-goal-or-attempt-mismatch";
    internal const string ProofIdentityMismatch = "solo-proof-identity-mismatch";
    internal const string BaselineUnavailable = "solo-baseline-unavailable";
    internal const string CheckMismatch = "solo-check-mismatch";
    internal const string CandidateNotIntroduced = "solo-candidate-not-introduced";
    internal const string AuthoritativeAttributionMismatch = "solo-authoritative-attribution-mismatch";
    internal const string TestBindingMismatch = "solo-test-binding-mismatch";
    internal const string BaselineCheckMismatch = "solo-baseline-check-mismatch";
    internal const string BaselineResultMismatch = "solo-baseline-result-mismatch";
    internal const string SourceAbsenceInvalid = "solo-source-absence-invalid";
    internal const string BaselineApparatusOrUnknown = "solo-baseline-apparatus-or-unknown";
    internal const string BaselineInheritedOrUncovered = "solo-baseline-inherited-or-uncovered";
    internal const string BaselineGreenUncovered = "solo-baseline-green-uncovered";
    internal const string DataCaseAmbiguous = "solo-data-case-ambiguous";
    internal const string CandidateRerunUnconfirmed = "solo-candidate-rerun-unconfirmed";
    internal const string FailingTestsAbsent = "solo-failing-tests-absent";
    internal const string EvidenceInvalid = "solo-evidence-invalid";
    internal const string DuplicateTestRow = "solo-duplicate-test-row";
    internal const string StartClaimedReconciliationRequired = "solo-start-claimed-reconciliation-required";
    internal const string NoAttributedFailure = "no-attributed-failure";
    internal const string UnrepresentableFailingTests = "unrepresentable-failing-tests";
    internal const string CandidateMismatch = "candidate-mismatch";
    internal const string ClassifiedAcceptanceFailure = "classified-acceptance-failure";
    internal const string NoCommittedDeveloper = "no-committed-developer";
    internal const string WorkerStillRunning = "worker-still-running";
    internal const string AlreadyRouted = "already-routed";
}
