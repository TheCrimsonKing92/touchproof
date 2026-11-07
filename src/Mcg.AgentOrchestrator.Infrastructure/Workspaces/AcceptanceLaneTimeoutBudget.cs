namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns only default-versus-estimate arithmetic; callers preserve explicit check timeouts.
internal static class AcceptanceLaneTimeoutBudget
{
    internal const double Multiplier = 1.5;

    internal static TimeSpan Resolve(TimeSpan configuredTimeout, double? estimateSeconds)
    {
        if (estimateSeconds is not { } estimate || !double.IsFinite(estimate) || estimate <= 0)
            return configuredTimeout;

        var scaledSeconds = Multiplier * estimate;
        if (scaledSeconds <= configuredTimeout.TotalSeconds)
            return configuredTimeout;

        var roundedSeconds = Math.Ceiling(scaledSeconds);
        return roundedSeconds >= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds(roundedSeconds);
    }
}
