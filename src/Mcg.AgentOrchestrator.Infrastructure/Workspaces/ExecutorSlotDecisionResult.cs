namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ExecutorSlotDecisionResult(string Action, string UnderlyingAction, int CurrentSlots,
    int ProposedSlots, bool Pinned, string Reason, int TotalSamples, int UnattributedSamples,
    IReadOnlyDictionary<string, int> IntervalSources,
    IReadOnlyList<ExecutorSlotEstimate> Estimates, ExecutorSlotWindow? Window,
    ExecutorSlotEstimate? ComparisonBaseline, ExecutorSlotEstimate? ComparisonTrial,
    int TrialProgress, ExecutorSlotControllerState NextState);
