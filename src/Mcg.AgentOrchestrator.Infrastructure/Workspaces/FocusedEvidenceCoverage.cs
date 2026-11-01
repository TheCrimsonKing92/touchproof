namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record FocusedEvidenceTargetCoverage(
    string Target,
    IReadOnlyList<string> CheckNames);

public sealed record FocusedEvidenceCoverage(
    IReadOnlyList<FocusedEvidenceTargetCoverage> TargetToChecks,
    string ExecutionMode = "focused",
    string ExecutionReason = "explicit-focused-mapping",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FocusedEvidenceDroppedSelection>? DroppedSelections = null);
