using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleGateProgress;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateProgressCalculation
{
    internal static (OwnerConsoleGateLane[] counted, OwnerConsoleGateLane[] done, double remaining,
        TimeSpan gateElapsed, double fraction, bool etaAvailable, int? overrunSeconds) Estimate(OwnerConsoleGateLane[] rows,
        int breakdownConcurrency, int executorSlots, AcceptanceGateLanePlan? plan, DateTimeOffset now, Gate gate)
    {
        var counted = rows.Where(OwnerConsoleGateCount.CountsProgress).ToArray();
        var total = counted.Sum(row => row.MedianSeconds ?? 0);
        var done = counted.Where(IsDone).ToArray();
        var unfinished = counted.Where(row => !IsDone(row)).ToArray();
        var concurrency = Math.Max(breakdownConcurrency + executorSlots,
            rows.Count(row => row.IsScheduled == true && !OwnerConsoleGateCount.IsBaseline(row) &&
                row.State == OwnerConsoleGateLaneState.Running));
        var etaAvailable = plan is not null && unfinished.All(row => row.MedianSeconds is > 0);
        // Baseline discovery is serial, while the scheduled lanes use executor slots.
        var baseline = unfinished.Where(OwnerConsoleGateCount.IsBaseline).ToArray();
        var remaining = etaAvailable ? Math.Max(
            OwnerConsoleGateRemainingEstimate.Makespan(unfinished.Where(row => !OwnerConsoleGateCount.IsBaseline(row)).ToArray(), concurrency),
            OwnerConsoleGateRemainingEstimate.Makespan(baseline, 1)) : 0;
        int? overrunSeconds = gate.Live && etaAvailable && unfinished.Length > 0 && remaining < 1
            ? (int)Math.Max(0, unfinished.Min(row => row.Elapsed.TotalSeconds - row.MedianSeconds!.Value)) : null;
        var gateElapsed = Nonnegative(now - gate.Start);
        var denominator = gateElapsed.TotalSeconds + remaining;
        // Display estimate only: gate wall time includes time before lanes start. Keep
        // the model's fractional remaining time and never imply unfinished work is done.
        var fraction = total > 0
            ? denominator > 0 ? gateElapsed.TotalSeconds / denominator : unfinished.Length == 0 ? 1 : 0
            : plan is not null && unfinished.Length == 0 ? 1 : 0;
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        if (unfinished.Length > 0) fraction = Math.Min(fraction, 0.99);
        if (!etaAvailable) fraction = 0;
        return (counted, done, remaining, gateElapsed, fraction, etaAvailable, overrunSeconds);
    }

    private static bool IsDone(OwnerConsoleGateLane row) => row.State is OwnerConsoleGateLaneState.Done or OwnerConsoleGateLaneState.Cached;
}
