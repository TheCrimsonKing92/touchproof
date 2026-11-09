namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Session-owned policy; the host owns event subscriptions and the polling timer.
internal sealed class OwnerConsoleLoopBackoff(TimeProvider clock, Action<int> setRate, int defaultRate) : IDisposable
{
    private const int IdleRate = 5;
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly int _activeRate = Math.Max(IdleRate, defaultRate);
    private DateTimeOffset _lastActivity = clock.GetUtcNow();
    private bool _idle;
    private bool _disposed;

    internal void OnInput() => OnRender();

    internal void OnRender()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _lastActivity = clock.GetUtcNow();
            if (!_idle) return;
            _idle = false;
            setRate(_activeRate);
        }
    }

    internal void Tick()
    {
        lock (_gate)
        {
            if (_disposed || _idle || clock.GetUtcNow() - _lastActivity < IdleAfter) return;
            _idle = true;
            setRate(IdleRate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            setRate(_activeRate);
        }
    }
}
