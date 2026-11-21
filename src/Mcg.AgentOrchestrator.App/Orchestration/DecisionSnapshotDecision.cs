using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record DecisionSnapshotDecision(
    IReadOnlyList<string> DispatchReadyTaskIds,
    string GateOutcome,
    string? DeclineReason,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GoalStatus>))] GoalStatus Status,
    GoalHoldState? HoldState,
    IReadOnlyDictionary<string, bool> CountsAsPassed)
{
    public const string CandidateBuilt = "candidate-built";
    public const string Declined = "declined";
}
