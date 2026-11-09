namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsolePaneRefresh
{
    internal static OwnerConsoleViewModel Apply(OwnerConsoleViewModel model, OwnerConsoleViewInputs inputs, TimeProvider clock,
        IReadOnlyDictionary<string, string> titles,
        Func<IReadOnlyDictionary<string, string>, OwnerConsoleGateProgress?>? gateProgress,
        Func<DateTimeOffset?>? latestTick, IConductorLiveness liveness, Func<string, string?>? gateMotion)
    {
        OwnerConsoleGateProgress? gate = null;
        DateTimeOffset? tick = null;
        try { gate = gateProgress?.Invoke(titles); } catch { /* Match full refresh observation failures. */ }
        try { tick = latestTick?.Invoke(); } catch { /* An unavailable tick is not a stale alarm. */ }
        foreach (var row in model.Board.Where(row => row.SortKey.Rank == OwnerConsoleAttention.GateRunning))
            gateMotion?.Invoke(row.GoalId);
        return model with { Gate = gate, Status = model.Status with
        {
            ConductorRunning = liveness.IsRunning(), LatestTickAt = tick, LandedToday = inputs.LandedToday,
            LastEventAge = inputs.LastConductEvent is { } at
                ? TimeSpan.FromTicks(Math.Max(0, (clock.GetUtcNow() - at).Ticks)) : null
        } };
    }
}
