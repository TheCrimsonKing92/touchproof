using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorHoldOwnerNextAction
{
    internal static (string Mechanism, string Actor, string Verb) For(ConductorHoldOwner owner) => owner switch
    {
        ConductorHoldOwner.AcceptanceQueue =>
            ("waiting for an acceptance gate slot", "conductor", "next-tick"),
        ConductorHoldOwner.BackgroundAttempt =>
            ("focused acceptance attempt running in the background", "conductor", "gate-reconciliation"),
        _ => ("unmapped", "unmapped", "unmapped")
    };
}
