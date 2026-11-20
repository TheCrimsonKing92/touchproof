namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class CanaryLoopDrain
{
    private readonly PostLandingCanaryCoordinator? _coordinator;
    private readonly TimeSpan _bound;
    private readonly TimeSpan _terminationGrace;
    private readonly TimeProvider _clock;
    private readonly Action<string> _progress;
    private CanaryExecution? _execution;
    private CanaryTaskDrain _drain;

    internal CanaryLoopDrain(PostLandingCanaryCoordinator? coordinator, TimeSpan bound,
        TimeSpan terminationGrace, TimeProvider clock, Action<string> loopProgress)
    {
        _coordinator = coordinator;
        _bound = bound;
        _terminationGrace = terminationGrace;
        _clock = clock;
        _progress = coordinator is null ? loopProgress : coordinator.ReportProgress;
        _execution = coordinator?.Execution;
        _drain = CreateDrain();
    }

    internal List<Task<PostLandingCanaryDisposition>> Tasks { get; } = [];
    internal object Gate { get; } = new();

    internal void Track(Task<PostLandingCanaryDisposition> task)
    {
        lock (Gate)
        {
            Tasks.Add(task);
        }
    }

    internal void Wait()
    {
        Task[] snapshot;
        lock (Gate)
        {
            snapshot = Tasks.Cast<Task>().Union(_execution?.Snapshot() ?? []).ToArray();
        }
        _drain.Wait(snapshot);
    }

    internal void ResetAfterDeclinedHandoff()
    {
        if (_execution is { Token.IsCancellationRequested: true })
        {
            var retiringTasks = _execution.Snapshot().ToHashSet();
            lock (Gate)
            {
                Tasks.RemoveAll(task => retiringTasks.Contains(task));
            }
            _coordinator!.RetireExecution(_execution);
            _execution = _coordinator.Execution;
            _ = _coordinator.ResumePending();
        }
        _drain = CreateDrain();
    }

    internal void DrainAndRetire()
    {
        try
        {
            Wait();
        }
        catch
        {
            // Preserve the primary loop exception. A timed-out wait observes later faults.
        }
        if (_execution is not null)
        {
            _coordinator!.RetireExecution(_execution);
        }
    }

    private CanaryTaskDrain CreateDrain() => new(_bound, _terminationGrace, _clock, _progress, _execution);
}

internal sealed class CanaryTaskDrain(
    TimeSpan bound,
    TimeSpan terminationGrace,
    TimeProvider clock,
    Action<string> progress,
    CanaryExecution? execution = null)
{
    internal const int DefaultBoundSeconds = 5;
    internal const int DefaultTerminationGraceSeconds = 5;

    private Task? _deadline;
    private Task? _terminationDeadline;
    private CanaryExecution.Runner[] _stoppingRunners = [];
    private bool _reportedTerminationExpiry;

    internal bool Wait(Task[] snapshot)
    {
        _deadline ??= Task.Delay(bound, clock);
        var all = Task.WhenAll(snapshot);
        Task.WhenAny(all, _deadline).GetAwaiter().GetResult();
        var pending = snapshot.Count(task => !task.IsCompleted);
        if (pending == 0)
        {
            all.GetAwaiter().GetResult();
            return true;
        }

        ObserveFault(all);
        if (_terminationDeadline is null && execution is not null)
        {
            _terminationDeadline = Task.Delay(terminationGrace, clock);
            _stoppingRunners = execution.Stop(_terminationDeadline);
        }

        progress(FormattableString.Invariant(
            $"CANARY_GATE result=drain-bound-hit pending={pending} boundSeconds={bound.TotalSeconds}"));
        if (_stoppingRunners.Length > 0)
        {
            var terminated = Task.WhenAll(_stoppingRunners.Select(runner => runner.Completion));
            Task.WhenAny(terminated, _terminationDeadline!).GetAwaiter().GetResult();
            var expired = _stoppingRunners
                .Where(runner => !runner.Completion.IsCompleted || runner.GraceExpired)
                .Select(runner => runner.LandingSha);
            var names = string.Join(",", expired);
            if (!_reportedTerminationExpiry && names.Length > 0)
            {
                _reportedTerminationExpiry = true;
                progress(FormattableString.Invariant(
                    $"CANARY_GATE result=termination-grace-expired runners={names} graceSeconds={terminationGrace.TotalSeconds}"));
            }
        }

        return false;
    }

    internal static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

// One coordinator's scheduled work, including recovery that can start before the loop takes ownership.
internal sealed class CanaryExecution
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _tasks = [];
    private readonly HashSet<Runner> _runners = [];
    private readonly TaskCompletionSource<Task> _terminationDeadline =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _stopCallbacks = Task.CompletedTask;
    private bool _retired;

    internal CanaryExecution() => Token = _stop.Token;

    internal CancellationToken Token { get; }

    internal Task<T> Start<T>(Func<Task<T>> run, T skipped)
    {
        lock (_gate)
        {
            if (_retired || Token.IsCancellationRequested)
            {
                return Task.FromResult(skipped);
            }

            _tasks.RemoveAll(task => task.IsCompletedSuccessfully);
            var task = Task.Run(run);
            _tasks.Add(task);
            return task;
        }
    }

    internal Task[] Snapshot()
    {
        lock (_gate)
        {
            return _tasks.ToArray();
        }
    }

    internal Runner RegisterRunner(string landingSha)
    {
        lock (_gate)
        {
            Token.ThrowIfCancellationRequested();
            var runner = new Runner(this, landingSha);
            _runners.Add(runner);
            return runner;
        }
    }

    internal Runner[] Stop(Task terminationDeadline)
    {
        lock (_gate)
        {
            _terminationDeadline.SetResult(terminationDeadline);
            _stopCallbacks = _stop.CancelAsync();
            return _runners.ToArray();
        }
    }

    internal void Retire()
    {
        Task all;
        lock (_gate)
        {
            _retired = true;
            all = Task.WhenAll(_tasks.Append(_stopCallbacks));
        }

        _ = all.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _stop.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal sealed class Runner(CanaryExecution owner, string landingSha) : IDisposable
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _graceExpired;

        internal string LandingSha { get; } = landingSha;
        internal CancellationToken StopToken => owner.Token;
        internal Task Completion => _completion.Task;
        internal bool GraceExpired => _graceExpired;

        internal async Task WaitForOutputAsync(Task output, Action cancelOutput)
        {
            var signal = owner._terminationDeadline.Task;
            if (!output.IsCompleted && await Task.WhenAny(output, signal).ConfigureAwait(false) == signal)
            {
                var deadline = await signal.ConfigureAwait(false);
                await Task.WhenAny(output, deadline).ConfigureAwait(false);
                if (!output.IsCompleted)
                {
                    _graceExpired = true;
                    CanaryTaskDrain.ObserveFault(output);
                    cancelOutput();
                    return;
                }
            }

            await output.ConfigureAwait(false);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                var signal = owner._terminationDeadline.Task;
                if (signal.IsCompletedSuccessfully && signal.Result.IsCompleted)
                {
                    _graceExpired = true;
                }
                _completion.TrySetResult();
                owner._runners.Remove(this);
            }
        }
    }
}
