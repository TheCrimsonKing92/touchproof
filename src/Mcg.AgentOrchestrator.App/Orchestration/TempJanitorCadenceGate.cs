namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class TempJanitorCadenceGate
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);

    private readonly TimeSpan minimumInterval;
    private readonly object sync = new();
    private long? lastPassStarted;

    internal TempJanitorCadenceGate(TimeSpan minimumInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimumInterval, TimeSpan.Zero);
        this.minimumInterval = minimumInterval;
    }

    internal bool TryBeginPass(TimeProvider clock)
    {
        lock (sync)
        {
            var now = clock.GetTimestamp();
            if (lastPassStarted is { } last && clock.GetElapsedTime(last, now) < minimumInterval)
                return false;

            lastPassStarted = now;
            return true;
        }
    }
}
