using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerActivityEventMap
{
    internal static bool Maps(OwnerConductEvent item) => item.EventKind switch
    {
        "goal-lifecycle" => Head(item) is "TaskDispatched" or "TaskCompleted" or "TaskFailed" or
            "HumanInputReceived" or "HumanInputSuperseded" || Field(item, "resolution-verb") is not null,
        "state-log-divergence" => Positive(item, "lost") || Positive(item, "repeated"),
        "author" => Field(item, "kind") == "ask-owner",
        "program-health" or "acceptance" or "acceptance-cohort" or "canary-gate" or "loop-relaunch" or "loop-handoff" or
            "loop-start" or "loop-stop" or "goal-escalation" or "goal-stalled" or "train-receipt-released" or
            "owner-question-resolved" or "owner-hold-cleared" or "admission" or "infrastructure-deferral" or "experiment-reading-due" or "test-impact-headroom-low" or "provider-hold-placed" or "provider-hold-probe" or "provider-hold-restored" or "provider-hold-overdue" or "provider-hold-check" => true,
        _ => false
    };
}
