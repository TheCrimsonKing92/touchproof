using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Observation of a completed selection decision, never an input to scheduling.
internal sealed record AcceptanceGateLanePlan(string SchemaVersion, string AttemptId,
    IReadOnlyList<string> Scheduled, IReadOnlyList<AcceptanceGateLaneSkip> Skipped,
    AcceptanceBalancedLaneLayout? BalancedLayout = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AcceptanceSelectedExecutionRecord? SelectedExecution { get; init; }
    public IReadOnlyDictionary<string, string> TestSettings { get; init; } = AcceptanceDeclaredTestSettings.Empty;
    public IReadOnlyList<AcceptanceHostTestSettings.Binding> LaneTestSettings { get; init; } = [];
    internal const string CurrentSchemaVersion = "gate-lane-plan.v1";
    internal const string ArtifactSuffix = ".lane-plan.json";
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    internal string FormatEventTarget() => "GATE_LANE_PLAN " + ToJson();
}
