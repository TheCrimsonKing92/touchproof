using Timeout = System.Threading.Timeout;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleNoticeExpiry(
    TimeProvider clock, Action<Action> invoke, Action render, CancellationToken token) : IDisposable
{
    private ITimer? _timer;

    internal void Rearm(TimeSpan? remaining)
    {
        if (remaining is null)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        // Create only when a success needs expiry; rendering failures or waiting for
        // owner input must not introduce a timer.
        _timer ??= clock.CreateTimer(_ =>
        {
            if (!token.IsCancellationRequested)
                invoke(() => { if (!token.IsCancellationRequested) render(); });
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // Recheck the clock at each callback: an early timer must rearm for the remainder.
        _timer.Change(remaining.Value, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _timer?.Dispose();
}
