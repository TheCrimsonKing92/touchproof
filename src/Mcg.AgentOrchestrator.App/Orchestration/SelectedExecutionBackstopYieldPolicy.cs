namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SelectedExecutionBackstopYieldPolicy
{
    // Operator's starting value on 2026-11-14; the owner may change it.
    internal const int MaximumUncoveredLandings = 5;
    // Operator's starting value on 2026-11-14; the owner may change it.
    internal const double MaximumOldestLandingAgeHours = 2;

    internal static string? Decide(int waitingCount, int uncoveredCount, TimeSpan? oldestLandingAge)
    {
        if (waitingCount == 0) return "idle";
        if (uncoveredCount >= MaximumUncoveredLandings) return "bound-landings";
        if (oldestLandingAge >= TimeSpan.FromHours(MaximumOldestLandingAgeHours)) return "bound-age";
        return null;
    }
}
