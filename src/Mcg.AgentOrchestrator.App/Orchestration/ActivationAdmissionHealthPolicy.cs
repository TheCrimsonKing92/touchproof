namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ActivationAdmissionHealthPolicy
{
    // Allow 3.9 minutes without a live gate and 5 minutes for census failures.
    // Tick frequency varies, so durations preserve the allowance as ticks get faster.
    // Require three observations as well so one long pause cannot trip a streak.
    internal const int MinimumStreakObservations = 3;
    internal static readonly TimeSpan OccupiedWithoutLiveGateAllowance = TimeSpan.FromSeconds(234);
    internal static readonly TimeSpan CensusFailureAllowance = TimeSpan.FromMinutes(5);
    // Ordinary gaps measured Nov 11/12 were 3–4 minutes; allow at least 2.5 times that.
    internal static readonly TimeSpan OwnerIdleLimit = TimeSpan.FromMinutes(10);
}
