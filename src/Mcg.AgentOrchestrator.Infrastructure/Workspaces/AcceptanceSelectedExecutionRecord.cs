namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceSelectedExecutionLane(string Name, string Disposition,
    IReadOnlyList<string> SelectedClasses, IReadOnlyList<string> SkippedClasses, string? Filter,
    string? LaneSkipReason = null, string? ClassSkipReason = null);

internal sealed record AcceptanceSelectedExecutionMethodFormResolution(string Selector, string ClassFullName);

// Counts describe distinct class identities in the candidate inventory, not test outcomes.
internal sealed record AcceptanceSelectedExecutionRecord(
    string Mode, string Decision, string? Outcome, string? FullGateReason,
    IReadOnlyList<string> FloorLanes, IReadOnlyList<string> GuardClasses,
    IReadOnlyList<string> ObligationClasses,
    IReadOnlyList<AcceptanceSelectedExecutionLane> Lanes,
    int? ExecutedClassCount, int? SkippedClassCount,
    int? ProposedExecutedClassCount, int? ProposedSkippedClassCount,
    double SelectionMilliseconds, bool StructuralCoverageScoped,
    bool Applied = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SuspensionReason = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? BackstopMissId = null)
{
    public IReadOnlyList<AcceptanceSelectedExecutionMethodFormResolution> MethodFormResolutions { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AcceptanceSelectedExecutionCheckCoverage>? WholeProjectCoverage { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AssemblyScanningGuardTestClasses.SelectionReason>? AssemblyScanReasons { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<AcceptanceUnresolvedObligationSelector> UnresolvedObligationSelectors { get; init; } = [];

    // Keep absent legacy fields empty in memory and omit empty collections on the wire.
    [System.Text.Json.Serialization.JsonPropertyName("unresolvedObligationSelectors")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<AcceptanceUnresolvedObligationSelector>? SerializedUnresolvedObligationSelectors
    {
        get => UnresolvedObligationSelectors.Count > 0 ? UnresolvedObligationSelectors : null;
        init => UnresolvedObligationSelectors = value ?? [];
    }

    internal static AcceptanceSelectedExecutionRecord Disabled { get; } =
        new("Off", "disabled", null, "Selected execution is disabled.", [], [], [], [], null, null, null, null, 0, false);
}
