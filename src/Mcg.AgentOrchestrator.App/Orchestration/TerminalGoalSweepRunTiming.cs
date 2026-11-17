using System.Globalization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record TerminalGoalSweepRunTiming(
    TimeSpan Run,
    TimeSpan Setup,
    TimeSpan CacheFlush,
    TimeSpan GoalLoopRemainder)
{
    public static TerminalGoalSweepRunTiming None { get; } = new(
        TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    // Test-only observation seam, scoped to the current async flow.
    internal static readonly AsyncLocal<Action?> CacheFlushProbe = new();

    internal static void BeforeCacheFlush(TerminalGoalSweepCache? cache)
    {
        if (cache is not null)
            CacheFlushProbe.Value?.Invoke();
    }

    internal static string FormatTickDetail(
        TerminalGoalSweepResult? result, IReadOnlyDictionary<string, long> elapsedByPhase)
    {
        if (result is null)
            return " sweep_owned_root_ms=0 sweep_setup_ms=0 sweep_cache_flush_ms=0 sweep_goal_loop_remainder_ms=0 sweep_run_ms=0 sweep_slowest=none sweep_unattributed_ms=0 sweep_outside_run_ms=0 sweep_cache_consulted=0";

        var timing = result.RunTiming;
        var internals = TimeSpan.FromMilliseconds(result.OwnedRootDurationMs) +
            TimeSpan.FromMilliseconds(result.GitIndexDurationMs) + TimeSpan.FromMilliseconds(result.EvidenceDurationMs) +
            TimeSpan.FromMilliseconds(result.EphemeralDurationMs) + TimeSpan.FromMilliseconds(result.AttentionDurationMs) +
            TimeSpan.FromMilliseconds(result.MergeEvidenceDurationMs) + TimeSpan.FromMilliseconds(result.GoalsDurationMs) +
            timing.Setup + timing.CacheFlush;
        var unattributed = timing.Run - internals;
        var outsideRun = TimeSpan.FromMilliseconds(elapsedByPhase.GetValueOrDefault("sweep")) - timing.Run;
        return string.Create(CultureInfo.InvariantCulture,
            $" sweep_owned_root_ms={result.OwnedRootDurationMs} sweep_setup_ms={(long)timing.Setup.TotalMilliseconds} sweep_cache_flush_ms={(long)timing.CacheFlush.TotalMilliseconds} sweep_goal_loop_remainder_ms={(long)timing.GoalLoopRemainder.TotalMilliseconds} sweep_run_ms={(long)timing.Run.TotalMilliseconds} sweep_slowest={StartupSweepStepsLine.FormatSlowest(result.GoalDurations)} sweep_unattributed_ms={(long)(unattributed < TimeSpan.Zero ? TimeSpan.Zero : unattributed).TotalMilliseconds} sweep_outside_run_ms={(long)(outsideRun < TimeSpan.Zero ? TimeSpan.Zero : outsideRun).TotalMilliseconds} sweep_cache_consulted={(result.CacheConsulted ? 1 : 0)}");
    }
}
