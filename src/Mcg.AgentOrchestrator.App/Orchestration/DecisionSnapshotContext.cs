using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record DecisionSnapshotContext(
    DateTimeOffset AsOf,
    string VerifiedTransition,
    int SlotIndex,
    string BuildIdentity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StateCopyHash = null);
