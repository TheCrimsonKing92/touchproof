namespace Mcg.AgentOrchestrator.App.Orchestration;

// CLI-owned: one serialized writer and one non-overlapping, bounded heartbeat. No event queue.
internal sealed class ImpactReplayRunSink : IImpactReplaySink, IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly TextWriter _error;
    private readonly ImpactReplayCheckpointStore _store;
    private readonly long _started;
    private long _phaseStarted;
    private long _lastHeartbeat;
    private readonly ITimer _timer;
    private ImpactReplayRunProgress _progress;
    private Exception? _failure;
    private ImpactReplayStoredAttempt? _attempt;
    private bool _stopped;
    internal string CheckpointPath => _store.Path;
    internal string RunId => _progress.RunId;
    internal ImpactReplayRunProgress Progress { get { lock (_gate) return _progress; } }

    internal ImpactReplayRunSink(string destination, ImpactReplayRunIdentity identity, TextWriter error,
        TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _error = error;
        _started = _phaseStarted = _lastHeartbeat = _clock.GetTimestamp();
        _progress = new(identity.RunId, "transition", ImpactReplayPhase.Startup, _clock.GetUtcNow(),
            0, 0, 0, identity.Configuration.RequestedLimit);
        // Startup is flushed even if opening the checkpoint fails. It never claims durable completed work.
        Write(_progress);
        _store = new(destination, identity, _progress);
        try { _timer = _clock.CreateTimer(_ => Heartbeat(), null, HeartbeatInterval, HeartbeatInterval); }
        catch (Exception ex)
        {
            try { _store.Finish(ImpactReplayRunState.Failed, _progress, [ex.ToString()]); }
            catch (Exception secondary) { Emergency($"Checkpoint failure; durability unconfirmed: {secondary.Message}; primary: {ex.Message}"); }
            finally { _store.Dispose(); }
            throw;
        }
    }

    public void Phase(ImpactReplayPhase phase) => Change(p => p with { Phase = phase }, resetPhase: true);
    public void InventoryDiscovered(int count) => Change(p => p with { DiscoveredInputs = count });
    public void InputExamined() => Change(p => p with { ExaminedInputs = p.ExaminedInputs + 1 });
    public void InventorySelected(int count) => Change(p => p with { EligibleAttempts = count });
    public void BeginCase(ImpactReplayStoredAttempt attempt, int ordinal)
    {
        Change(p => p with
        {
            Phase = ImpactReplayPhase.CaseSelection, CaseOrdinal = ordinal, AttemptId = attempt.AttemptId,
            GoalId = attempt.GoalId, ExaminedCases = p.ExaminedCases + 1
        }, resetPhase: true);
        _attempt = attempt;
    }

    public void Diagnostic(ImpactReplayDiagnostic diagnostic) => Change(p => p with { Skipped = p.Skipped + 1 },
        persist: p =>
        {
            try { _store.AddDiagnostic(diagnostic, p); }
            catch (Exception ex)
            {
                // Rollback removes the diagnostic payload too; retain it in the terminal failure evidence.
                throw new ImpactReplayObservationException(
                    $"Impact replay diagnostic persistence failed: {ImpactReplayCheckpointStore.Serialize(diagnostic)}", ex);
            }
        });

    public void CompleteCase(ImpactReplayCaseResult result)
    {
        if ((result.Kind == ImpactReplayCaseKind.Replayable) != (result.Record is not null) ||
            (result.Kind != ImpactReplayCaseKind.Replayable && result.Diagnostic is null))
            throw new InvalidDataException("Invalid replay case outcome.");
        Change(p => p with
        {
            Phase = ImpactReplayPhase.CaseCompletion, Completed = p.Completed + 1,
            Replayable = p.Replayable + (result.Kind == ImpactReplayCaseKind.Replayable ? 1 : 0),
            Skipped = p.Skipped + (result.Kind == ImpactReplayCaseKind.Skipped ? 1 : 0),
            Unreplayable = p.Unreplayable + (result.Kind == ImpactReplayCaseKind.Unreplayable ? 1 : 0)
        }, resetPhase: true, persist: p => _store.CompleteCase(_attempt!, result, p));
    }

    public void OwnCheckout(string path) => Execute(() => _store.OwnCheckout(_progress.CaseOrdinal!.Value, path));
    public void CheckoutCleaned(string path, string status, string? error)
    {
        lock (_gate)
        {
            // Cleanup evidence is still worth recording after a failed observation write.
            try { _store.Cleanup(_progress.CaseOrdinal!.Value, path, status, error); }
            catch (Exception ex)
            {
                Emergency($"Owned checkout cleanup: path={path}; status={status}; error={error}; durability unconfirmed: {ex.Message}");
                throw new ImpactReplayObservationException("Impact replay cleanup checkpoint failed.", ex);
            }
        }
    }

    internal void FinalizeReport(ImpactReplayReport report)
    {
        Phase(ImpactReplayPhase.Finalizing);
        Execute(() =>
        {
            _store.Reconcile(report, _progress);
            _store.Finish(ImpactReplayRunState.Finalizing, _progress, []);
        });
    }

    internal void Complete() => Execute(() =>
    {
        var next = Stamp(_progress with { Phase = ImpactReplayPhase.Finished }, "transition", true);
        _store.Finish(ImpactReplayRunState.Completed, next, []);
        _progress = next;
        _stopped = true;
        Write(next);
    });

    internal void Fail(Exception primary, bool cancelled)
    {
        lock (_gate)
        {
            _stopped = true;
            var errors = new List<string> { primary.ToString() };
            if (_failure is not null && !ReferenceEquals(primary, _failure)) errors.Add(_failure.ToString());
            try
            {
                _store.Finish(cancelled ? ImpactReplayRunState.Cancelled : ImpactReplayRunState.Failed,
                    Stamp(_progress, "transition", true), errors);
            }
            catch (Exception ex)
            {
                Emergency($"Checkpoint failure; durability unconfirmed: {ex.Message}; primary: {primary.Message}");
            }
        }
    }

    internal void Heartbeat()
    {
        // A slow writer must not accumulate timer callbacks waiting for the same lock.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_stopped || _failure is not null) return;
            var now = _clock.GetTimestamp();
            if (_clock.GetElapsedTime(_lastHeartbeat, now) < HeartbeatInterval ||
                _clock.GetElapsedTime(_phaseStarted, now) < HeartbeatInterval) return;
            try
            {
                var next = Stamp(_progress, "heartbeat", false);
                _store.Observe(next);
                _progress = next;
                _lastHeartbeat = now;
                Write(next);
            }
            catch (Exception ex)
            {
                _failure = new ImpactReplayObservationException("Impact replay heartbeat failed; durability unconfirmed.", ex);
                Emergency($"{_failure.Message} {ex.Message}");
            }
        }
        finally { Monitor.Exit(_gate); }
    }

    private void Change(Func<ImpactReplayRunProgress, ImpactReplayRunProgress> change,
        bool resetPhase = false, Action<ImpactReplayRunProgress>? persist = null) => Execute(() =>
    {
        var next = change(_progress);
        if (resetPhase) _phaseStarted = _clock.GetTimestamp();
        next = Stamp(next, "transition", true);
        (persist ?? _store.Observe)(next);
        _progress = next;
        Write(next);
    });

    private ImpactReplayRunProgress Stamp(ImpactReplayRunProgress progress, string kind, bool advances)
    {
        var now = _clock.GetTimestamp();
        var elapsed = _clock.GetElapsedTime(_started, now).TotalSeconds;
        return progress with
        {
            Kind = kind, Timestamp = _clock.GetUtcNow(), RunElapsedSeconds = elapsed,
            PhaseElapsedSeconds = _clock.GetElapsedTime(_phaseStarted, now).TotalSeconds,
            LastProgressElapsedSeconds = advances ? elapsed : progress.LastProgressElapsedSeconds
        };
    }

    private void Execute(Action action)
    {
        lock (_gate)
        {
            if (_failure is not null) throw _failure;
            if (_stopped) throw new InvalidOperationException("Impact replay observation has stopped.");
            try { action(); }
            catch (Exception ex)
            {
                _failure = new ImpactReplayObservationException("Impact replay observation failed; durability unconfirmed.", ex);
                throw _failure;
            }
        }
    }

    private void Write(ImpactReplayRunProgress progress)
    {
        _error.WriteLine(ImpactReplayCheckpointStore.Serialize(progress));
        _error.Flush();
    }

    private void Emergency(string message)
    {
        try { _error.WriteLine(message); _error.Flush(); }
        catch { /* The primary observation failure remains available to the foreground owner. */ }
    }

    public void Dispose()
    {
        lock (_gate) _stopped = true;
        _timer.Dispose();
        lock (_gate) _store.Dispose();
    }
}
