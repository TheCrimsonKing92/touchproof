namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SoloHoldNextAction
{
    internal static (string Mechanism, string Owner, string NextAction) For(string reason)
    {
        if (reason.Contains("solo-duplicate-test-row", StringComparison.Ordinal))
            return ("solo acceptance failure reproduction", "Developer", "route the Developer because the failure reproduced");

        return reason switch
        {
            "solo-evidence-awaiting-consumption" =>
                ("solo evidence consumption", "conductor", "wait for the next tick"),
            "solo-acceptance-retry-cap" =>
                ("solo acceptance correction retry cap", "operator", "review the solo acceptance correction evidence"),
            _ => ("unmapped", "unmapped", "unmapped")
        };
    }
}
