namespace Mcg.AgentOrchestrator.Infrastructure;

public static class AcceptanceFailureClassifications
{
    public const string GateEnvironmentInterference = "gate-environment-interference";
    public const string InheritedBaselineApparatus = "inherited-baseline-apparatus";
    public const string StructuralCoverageFailed = "structural-coverage-failed";
    public const string FocusedSelectionApparatusFailure = "focused-selection-apparatus-failure";
    public const string FocusedSelectionAbsentAtBaseline = "focused-selection-absent-at-baseline";
    public const string RetryEvidenceRetentionFailed = "retry-evidence-retention-failed";
    public const string FocusedSelectionReceiptUnreadable = "focused-selection-receipt-unreadable";
    public const string SeededRepositoryProcessOutputApparatus = "seeded-repository-process-output-apparatus";
    public const string SeededRepositoryApparatus = "seeded-repository-apparatus";
    public const string SharedGateApparatusInvalidated = "shared-gate-apparatus-invalidated";
    public const string AssemblyCleanupFailure = "assembly-cleanup-failure";
    public const string BuildPhaseTimeout = "build-phase-timeout";
    public const string MissingTool = "missing-tool";

    public static bool IsEnvironmentalApparatus(string? classification) =>
        classification is GateEnvironmentInterference or
            InheritedBaselineApparatus or
            FocusedSelectionApparatusFailure or
            FocusedSelectionReceiptUnreadable or
            SeededRepositoryProcessOutputApparatus or
            SeededRepositoryApparatus or
            AssemblyCleanupFailure or
            BuildPhaseTimeout or
            MissingTool or
            SharedGateApparatusInvalidated;
}
