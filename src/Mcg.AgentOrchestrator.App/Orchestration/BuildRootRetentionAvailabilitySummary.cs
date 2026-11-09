namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BuildRootRetentionAvailabilitySummary
{
    public static string Partial(string terminalSummaryLine, IReadOnlyList<UnavailableGoalStore> unavailable) =>
        $"{terminalSummaryLine} unavailable-stores={string.Join(",", unavailable.Select(store => $"{store.Project}:{store.Failure}"))} unstored-skipped=goal-store-unavailable";

    public static string NoneReadable(int retentionDays, IReadOnlyList<UnavailableGoalStore> unavailable) =>
        Partial(FormattableString.Invariant(
            $"SWEEP_BUILD_ROOT_RETENTION window-days={retentionDays} reclaimed=0 bytes=0 skipped=0 reasons=goal-store-unavailable"), unavailable);
}
