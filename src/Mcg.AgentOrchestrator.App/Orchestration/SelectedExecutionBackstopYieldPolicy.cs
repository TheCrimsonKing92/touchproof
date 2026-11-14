namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SelectedExecutionBackstopYieldPolicy
{
    // Operator's starting value on 2026-11-14; the owner may change it.
    internal const int MaximumUncoveredLandings = 5;
    // Operator's starting value on 2026-11-14; the owner may change it.
    internal const double MaximumOldestLandingAgeHours = 2;

    internal static string? Decide(int waitingCount, int uncoveredCount, TimeSpan? oldestLandingAge,
        SelectedExecutionBackstopYieldBounds bounds)
    {
        if (waitingCount == 0) return "idle";
        if (uncoveredCount >= bounds.MaxUncoveredLandings) return "bound-landings";
        if (oldestLandingAge >= TimeSpan.FromHours(bounds.MaxOldestLandingAgeHours)) return "bound-age";
        return null;
    }
}
