namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleLoopRun(
    OwnerConsoleLoop loop, IOwnerConsoleSteps steps, IOwnerConsoleInput input,
    IConductEventSource events, TimeProvider clock, OwnerConsoleLoopOptions options,
    RefreshNoticePolicy refreshNotices)
{
    private CancellationToken _token;
    private Task<string?>? _lineTask;
    private Task<OwnerConductEvent>? _eventTask;
    private OwnerConductEvent? _pendingEvent;
    private Task? _unfinishedRefresh;
    private readonly List<Task> _lateSteps = new();

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _token = linked.Token;
        try
        {
            await RunStepAsync("startup", async ct =>
            {
                await steps.StartAsync(events.LastActivity, ct);
                return true;
            }, collectEvents: false);
            await ReadLoopAsync();
        }
        finally
        {
            linked.Cancel();
            if (_eventTask is not null)
            {
                try { await _eventTask.WaitAsync(options.ShutdownBound, clock, CancellationToken.None); }
                catch (TimeoutException) { loop.MarkEventSourceAbandoned(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { loop.ReportError(ex); }
            }
            try { await events.DisposeAsync().AsTask().WaitAsync(options.ShutdownBound, clock, CancellationToken.None); }
            catch (TimeoutException) { loop.MarkEventSourceAbandoned(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { loop.ReportError(ex); }
        }
    }

    private async Task ReadLoopAsync()
    {
        while (true)
        {
            _token.ThrowIfCancellationRequested();
            _lineTask ??= input.ReadLineAsync(_token).AsTask();
            _eventTask ??= ReadEventAsync();
            _lateSteps.RemoveAll(task => task.IsCompleted);

            // WhenAny's argument order cannot establish command priority.
            if (_lineTask.IsCompleted)
            {
                var line = await _lineTask;
                _lineTask = null;
                if (line is null) break;
                if (await RunStepAsync(line.Trim(), ct => steps.HandleCommandAsync(line, ct)) == false)
                    break;
                continue;
            }
            if (_eventTask.IsCompleted)
                await CollectEventAsync();
            if (_pendingEvent is not null && !input.IsEditingLine &&
                (_unfinishedRefresh is null || _unfinishedRefresh.IsCompleted))
            {
                if (_lineTask.IsCompleted) continue;
                var item = _pendingEvent;
                _pendingEvent = null;
                await RunStepAsync("refresh after conductor event", async ct =>
                {
                    await steps.HandleEventAsync(item, ct);
                    return true;
                }, isRefresh: true);
                continue;
            }
            _eventTask ??= ReadEventAsync();
            try
            {
                await Task.WhenAny(_lateSteps.Concat([_lineTask, _eventTask]))
                    .WaitAsync(options.PollBound, clock, _token);
            }
            catch (TimeoutException) { }
        }
    }

    private async Task<OwnerConductEvent> ReadEventAsync()
    {
        // Normalize synchronous source throws into the same faulted-read path.
        return await events.ReadAsync(_token);
    }

    private async Task<OwnerConductEvent> RetryEventAsync()
    {
        await Task.Delay(options.PollBound, clock, _token);
        return await ReadEventAsync();
    }

    private async Task<bool> CollectEventAsync()
    {
        var read = _eventTask!;
        _eventTask = null;
        try
        {
            var latest = await read;
            // Preserve the board trigger from OwnerConsoleSession.HandleEventAsync
            // while retaining the latest event's timestamp and other data.
            _pendingEvent = _pendingEvent is not null && OwnerConsoleLoop.PrintsBoard(_pendingEvent.EventKind) &&
                !OwnerConsoleLoop.PrintsBoard(latest.EventKind)
                ? latest with { EventKind = _pendingEvent.EventKind }
                : latest;
            return true;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Keep the retry pending so input and active steps can progress.
            // Arm the delay before publishing the error, including under fake time.
            _eventTask = RetryEventAsync();
            loop.ReportError(ex);
            return false;
        }
    }

    private async Task<bool?> RunStepAsync(string label, Func<CancellationToken, Task<bool>> action,
        bool isRefresh = false, bool collectEvents = true)
    {
        var stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(_token);
        using var timers = CancellationTokenSource.CreateLinkedTokenSource(_token);
        // Arm timers before invoking the step, including in fake-time tests.
        var busy = RefreshNoticePolicy.ShouldAnnounceBusy(isRefresh)
            ? Task.Delay(options.BusyNoticeAfter, clock, timers.Token) : null;
        var bound = Task.Delay(options.OperationBound, clock, timers.Token);
        Task<bool>? operation = null;
        var detached = false;
        try
        {
            // Session adapters can perform synchronous store I/O before their first await.
            // Keep that work off the loop so its notices and bounds remain observable.
            var stepToken = stepCancellation.Token;
            operation = Task.Run(() => action(stepToken), stepToken);
            if (await WaitForStepAsync(label, operation, busy, bound, collectEvents))
            {
                loop.Announce(isRefresh ? refreshNotices.TimedOut(options.OperationBound) :
                    $"{label} did not finish within {options.OperationBound.TotalSeconds:0.###}s; the console is still running");
                detached = true;
                DetachTimedOutStep(label, operation, stepCancellation, isRefresh);
                return null;
            }
            await DrainEventBurstAsync(collectEvents);
            var result = await operation;
            if (isRefresh) loop.Announce(refreshNotices.Succeeded());
            return result;
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            if (operation is not null)
            {
                detached = true;
                _lateSteps.Add(ObserveLateStepAsync(label, operation, stepCancellation, isRefresh));
            }
            throw;
        }
        catch (OperationCanceledException)
        {
            loop.Announce(isRefresh ? refreshNotices.Failed("refresh was abandoned") : $"{label} was abandoned");
            return null;
        }
        catch (Exception ex)
        {
            loop.ReportStepError(ex, isRefresh);
            return null;
        }
        finally
        {
            timers.Cancel();
            if (!detached) stepCancellation.Dispose();
        }
    }

    private async Task<bool> WaitForStepAsync(string label, Task<bool> operation, Task? busy, Task bound, bool collectEvents)
    {
        var busyPrinted = false;
        while (!operation.IsCompleted)
        {
            _token.ThrowIfCancellationRequested();
            if (!busyPrinted && busy is { IsCompleted: true })
            {
                loop.Announce($"working: {label} ...");
                busyPrinted = true;
            }
            if (bound.IsCompleted) return true;
            // Drain events into one pending refresh while a step is running.
            if (collectEvents)
            {
                _eventTask ??= ReadEventAsync();
                if (_eventTask.IsCompleted)
                {
                    await CollectEventAsync();
                    continue;
                }
            }
            var waits = new List<Task> { operation, bound };
            if (!busyPrinted && busy is not null) waits.Add(busy);
            if (collectEvents) waits.Add(_eventTask!);
            await Task.WhenAny(waits).WaitAsync(_token);
        }
        return false;
    }

    private void DetachTimedOutStep(string label, Task<bool> operation, CancellationTokenSource stepCancellation, bool isRefresh)
    {
        try { stepCancellation.Cancel(); }
        catch (Exception ex) { loop.ReportStepError(ex, isRefresh); }
        var observer = ObserveLateStepAsync(label, operation, stepCancellation, isRefresh);
        _lateSteps.Add(observer);
        if (isRefresh) _unfinishedRefresh = observer;
    }

    private async Task DrainEventBurstAsync(bool collectEvents)
    {
        // Finish draining an already-started burst before the next refresh.
        // Do not initiate a read here for a synchronous completed step.
        while (collectEvents && _eventTask is { IsCompleted: true })
        {
            _token.ThrowIfCancellationRequested();
            if (!await CollectEventAsync()) break;
            _eventTask = ReadEventAsync();
        }
    }

    private async Task ObserveLateStepAsync(string label, Task<bool> operation, CancellationTokenSource source, bool isRefresh)
    {
        try
        {
            await operation; // Successful steps print their own answers, including late ones.
            if (isRefresh) loop.Announce(refreshNotices.Succeeded());
        }
        catch (OperationCanceledException)
        { loop.Announce(isRefresh ? refreshNotices.Failed("refresh was abandoned") : $"{label} was abandoned"); }
        catch (Exception ex) { loop.ReportStepError(ex, isRefresh); }
        finally { source.Dispose(); }
    }
}
