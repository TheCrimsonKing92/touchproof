namespace Mcg.AgentOrchestrator.Infrastructure;

// Only this queue hands waiting runs an atomic pool claim. Timers bound each registration;
// neither timer callbacks nor slot-release signals can grant an expired or cancelled waiter.
internal sealed class FocusedEvidenceExecutorWaitQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];
    private readonly Func<RemoteLaneExecutorEntry?> _claim;
    private readonly Action<RemoteLaneExecutorEntry> _release;
    private readonly TimeProvider _clock;
    private readonly ITimer _poll;
    private bool _disposed;

    internal FocusedEvidenceExecutorWaitQueue(Func<RemoteLaneExecutorEntry?> claim,
        Action<RemoteLaneExecutorEntry> release, TimeProvider clock, TimeSpan pollInterval)
    {
        if (pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _claim = claim;
        _release = release;
        _clock = clock;
        _poll = clock.CreateTimer(_ => Signal(), null, pollInterval, pollInterval);
    }

    internal Task<(RemoteLaneExecutorEntry? Entry, FocusedEvidenceExecutorWaitRecord Record)> RegisterAsync(
        string key, double predictedSeconds, TimeSpan bound, CancellationToken token, string? priorityKey = null)
    {
        if (!FocusedEvidenceGroupCostModel.IsHeavy(predictedSeconds) || bound <= TimeSpan.Zero ||
            bound.TotalSeconds > predictedSeconds || bound.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(bound));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var waiter = new Waiter(key, priorityKey ?? key, predictedSeconds, bound, token, _clock.GetUtcNow(), _clock.GetTimestamp());
            _waiters.Add(waiter);
            waiter.Deadline = _clock.CreateTimer(_ =>
            {
                // Publish before taking the lock: a slow claim must not outrun its timer.
                Volatile.Write(ref waiter.Expired, true);
                lock (_gate) Complete(waiter, null, "expired-local-fallback");
            }, null, bound, Timeout.InfiniteTimeSpan);
            waiter.Cancellation = token.Register(() =>
            {
                lock (_gate) Complete(waiter, null, "cancelled");
            });
            if (waiter.Completion.Task.IsCompleted) waiter.Cancellation.Unregister();
            return waiter.Completion.Task;
        }
    }

    // Called by a pool release, and periodically for slots released by another attempt or a gate.
    internal void Signal()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var waiter in _waiters.OrderByDescending(item => item.PredictedSeconds)
                         .ThenBy(item => item.PriorityKey, StringComparer.Ordinal).ToArray())
            {
                if (Terminal(waiter) is { } terminal) { Complete(waiter, null, terminal); continue; }
                RemoteLaneExecutorEntry? entry;
                try { entry = _claim(); }
                catch (Exception) { Complete(waiter, null, "failed"); continue; }
                if (entry is null) continue;
                if (Terminal(waiter) is { } afterClaim)
                {
                    try { _release(entry); Complete(waiter, null, afterClaim); }
                    catch (Exception) { Complete(waiter, null, "failed"); }
                }
                else Complete(waiter, entry, "acquired");
            }
        }
    }

    private string? Terminal(Waiter waiter) => waiter.Token.IsCancellationRequested ? "cancelled" :
        Volatile.Read(ref waiter.Expired) || _clock.GetElapsedTime(waiter.Timestamp) >= waiter.Bound
            ? "expired-local-fallback" : null;

    private void Complete(Waiter waiter, RemoteLaneExecutorEntry? entry, string outcome)
    {
        if (!_waiters.Remove(waiter))
        {
            if (entry is not null) throw new InvalidOperationException("A completed waiter cannot receive an executor.");
            return;
        }
        waiter.Deadline?.Dispose();
        waiter.Cancellation.Unregister();
        var ended = _clock.GetUtcNow();
        waiter.Completion.SetResult((entry, new(waiter.Key, waiter.Started, ended,
            ended >= waiter.Started ? ended - waiter.Started : TimeSpan.Zero, waiter.Bound, outcome, entry?.Id)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _poll.Dispose();
            foreach (var waiter in _waiters.ToArray()) Complete(waiter, null, "cancelled");
        }
    }

    private sealed class Waiter(string key, string priorityKey, double seconds, TimeSpan bound, CancellationToken token,
        DateTimeOffset started, long timestamp)
    {
        internal string Key { get; } = key;
        internal string PriorityKey { get; } = priorityKey;
        internal double PredictedSeconds { get; } = seconds;
        internal TimeSpan Bound { get; } = bound;
        internal CancellationToken Token { get; } = token;
        internal DateTimeOffset Started { get; } = started;
        internal long Timestamp { get; } = timestamp;
        internal bool Expired;
        internal ITimer? Deadline { get; set; }
        internal CancellationTokenRegistration Cancellation { get; set; }
        internal TaskCompletionSource<(RemoteLaneExecutorEntry? Entry, FocusedEvidenceExecutorWaitRecord Record)> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
