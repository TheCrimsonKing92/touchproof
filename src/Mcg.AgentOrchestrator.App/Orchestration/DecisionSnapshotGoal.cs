using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record DecisionSnapshotGoal(
    string GoalId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DecisionSnapshotDecision? Decision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DecisionSnapshotException? Exception = null);
