using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Composes explicit-timeout precedence and the lane estimate before applying the budget arithmetic.
internal static class AcceptanceLaneCheckTimeout
{
    internal static TimeSpan Resolve(AcceptanceManifestCheck? check, AcceptanceGateEngineSettings settings)
    {
        if (check?.TimeoutMinutes is > 0)
            return settings.ResolveCheckTimeout(check.TimeoutMinutes);

        var configuredTimeout = settings.ResolveCheckTimeout(null);
        if (check is null) return configuredTimeout;
        var local = AcceptanceLaneDurationStore.ResolveLocalEstimate(check);
        var estimateSeconds = RemoteLaneOfferPolicy.ResolveLocalSeconds(
            local.MedianSeconds, local.Samples, check.EstimatedSerialSeconds);
        return AcceptanceLaneTimeoutBudget.Resolve(configuredTimeout, estimateSeconds);
    }
}
