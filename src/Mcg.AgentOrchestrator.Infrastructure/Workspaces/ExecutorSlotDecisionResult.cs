namespace Mcg.AgentOrchestrator.Infrastructure;

// NextState is for the pure caller. The audit row persists the selected checkpoint once.
internal sealed record ExecutorSlotDecisionResult(string Action, string UnderlyingAction, int CurrentSlots,
    int ProposedSlots, bool Pinned, string Reason, int TotalSamples, int UnattributedSamples,
    IReadOnlyDictionary<string, int> IntervalSources,
    IReadOnlyList<ExecutorSlotEstimate> Estimates, ExecutorSlotWindow? Window,
    ExecutorSlotEstimate? ComparisonBaseline, ExecutorSlotEstimate? ComparisonTrial,
    int TrialProgress, [property: System.Text.Json.Serialization.JsonIgnore] ExecutorSlotControllerState NextState);
