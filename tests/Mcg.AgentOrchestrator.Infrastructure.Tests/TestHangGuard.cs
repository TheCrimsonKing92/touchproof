internal static class TestHangGuard
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan HangSafetyBound = TimeSpan.FromSeconds(120);
    // Whole-test hang failsafe below the 600 s lane stall limit; never a speed check.
    internal const int WholeTestMilliseconds = 300_000;

    internal static async Task CompletesWithinAsync(Task task, TimeSpan bound, string operation)
    {
        await AwaitTerminalAsync(task, bound, stall => WithinBoundFailure(bound, operation, stall));
    }

    internal static async Task WaitAsync(Task task, string eventName)
    {
        await AwaitTerminalAsync(task, Bound, stall => new TimeoutException($"Hang guard: {eventName} {stall}."));
        await task;
    }

    internal static async Task<T> WaitAsync<T>(Task<T> task, string eventName)
    {
        await AwaitTerminalAsync(task, Bound, stall => new TimeoutException($"Hang guard: {eventName} {stall}."));
        return await task;
    }

    internal static async Task CompletesWithinAsync(Task task, TimeSpan bound, string operation,
        Func<TimeSpan> now, Func<InProcessProgressWatch.Sample> sample, Action<Func<bool>> pace)
    {
        await AwaitTerminalAsync(task, bound, stall => WithinBoundFailure(bound, operation, stall), now, sample, pace);
    }

    internal static async Task WaitAsync(Task task, string eventName,
        Func<TimeSpan> now, Func<InProcessProgressWatch.Sample> sample, Action<Func<bool>> pace)
    {
        await AwaitTerminalAsync(task, Bound, stall => new TimeoutException($"Hang guard: {eventName} {stall}."), now, sample, pace);
        await task;
    }

    internal static async Task<T> WaitAsync<T>(Task<T> task, string eventName,
        Func<TimeSpan> now, Func<InProcessProgressWatch.Sample> sample, Action<Func<bool>> pace)
    {
        await AwaitTerminalAsync(task, Bound, stall => new TimeoutException($"Hang guard: {eventName} {stall}."), now, sample, pace);
        return await task;
    }

    private static Exception WithinBoundFailure(TimeSpan bound, string operation, InProcessProgressWatch.Stall stall) =>
        new TestHangGuardExpiredException(
            $"Test hang-safety bound of {bound} expired: {operation} did not reach a terminal state. {stall}.");

    private static async Task AwaitTerminalAsync(Task task, TimeSpan window,
        Func<InProcessProgressWatch.Stall, Exception> expired, Func<TimeSpan>? now = null,
        Func<InProcessProgressWatch.Sample>? sample = null, Action<Func<bool>>? pace = null)
    {
        if (task.IsCompleted) return;
        var elapsed = TimeSpan.Zero;
        var watch = new InProcessProgressWatch(sample ?? InProcessProgressWatch.ReadHost, window, now ?? (() => elapsed));
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Step()
        {
            if (task.IsCompleted || failure.Task.IsCompleted) return true;
            try
            {
                var stall = watch.Observe();
                if (!task.IsCompleted && stall is not null)
                    failure.TrySetException(expired(stall));
            }
            catch (Exception exception)
            {
                failure.TrySetException(exception);
            }
            return task.IsCompleted || failure.Task.IsCompleted;
        }

        SamplingRegistration? registration = null;
        try
        {
            if (!Step())
            {
                if (pace is not null)
                    pace(Step);
                else
                    registration = SamplingThread.Register(slice =>
                    {
                        // Scheduling delay is not evidence of inactivity: credit only the waited slice.
                        elapsed += slice;
                        return Step() ? null : watch.NextSlice;
                    }, watch.NextSlice);
            }
            await Task.WhenAny(task, failure.Task);
            // Completion wins even when a sample and task termination happen in the same slice.
            if (!task.IsCompleted) await failure.Task;
            else if (failure.Task.IsFaulted) _ = failure.Task.Exception;
        }
        finally
        {
            registration?.Dispose();
        }
    }

    private sealed class SamplingRegistration(Func<TimeSpan, TimeSpan?> tick, TimeSpan nextSlice) : IDisposable
    {
        internal TimeSpan NextSlice = nextSlice;
        internal TimeSpan? Tick(TimeSpan slice) => tick(slice);
        public void Dispose() => SamplingThread.Remove(this);
    }

    // A timer/Task.Delay would itself complete pool work items and be mistaken for progress.
    // This thread samples only; every completion source prevents inline caller continuations.
    private static class SamplingThread
    {
        private static readonly object Sync = new();
        private static readonly List<SamplingRegistration> Pending = [];

        static SamplingThread()
        {
            new Thread(Run) { IsBackground = true, Name = "Test hang guard progress sampler" }.Start();
        }

        internal static SamplingRegistration Register(Func<TimeSpan, TimeSpan?> tick, TimeSpan slice)
        {
            var registration = new SamplingRegistration(tick, slice);
            lock (Sync)
            {
                Pending.Add(registration);
                Monitor.Pulse(Sync);
            }
            return registration;
        }

        internal static void Remove(SamplingRegistration registration)
        {
            lock (Sync)
            {
                Pending.Remove(registration);
                Monitor.Pulse(Sync); // Cancel the pending slice when its awaited task terminates.
            }
        }

        private static void Run()
        {
            while (true)
            {
                SamplingRegistration[] pending;
                TimeSpan slice;
                TimeSpan waited;
                lock (Sync)
                {
                    while (Pending.Count == 0) Monitor.Wait(Sync);
                    pending = Pending.ToArray();
                    slice = pending.Min(item => item.NextSlice);
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    // A monitor wait uses no pool timer and can be interrupted by removal
                    // or a newly registered wait with a shorter sampling slice.
                    var interrupted = Monitor.Wait(Sync, slice);
                    waited = interrupted
                        ? TimeSpan.FromTicks(Math.Min(slice.Ticks, System.Diagnostics.Stopwatch.GetElapsedTime(started).Ticks))
                        : slice;
                }
                foreach (var registration in pending)
                {
                    var next = registration.Tick(waited);
                    lock (Sync)
                    {
                        if (next is null) Pending.Remove(registration);
                        else registration.NextSlice = next.Value;
                    }
                }
            }
        }
    }
}

internal sealed class TestHangGuardExpiredException(string message) : Exception(message);
