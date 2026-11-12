using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ProgressiveReviewGlanceTests;

// Pool settings are process-wide. This collection excludes every other collection in this host;
// coordinators and their fakes belong exclusively to each arm.
[Collection("ProgressiveReviewGlanceCompletionHandoff")]
public sealed class ProgressiveReviewGlanceCompletionHandoffTests(ITestOutputHelper output)
{
    [CollectionDefinition("ProgressiveReviewGlanceCompletionHandoff", DisableParallelization = true)]
    [ProcessLocalTestCollection("Only this test process's worker pool is saturated; no machine-wide resource is changed.")]
    public sealed class SerialCollection;

    private static readonly TimeSpan HistoricalWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionFailsafe = TimeSpan.FromSeconds(30);

    [Fact]
    public void ReleaseThenObserve_SeparatesContinuationLatencyFromHarvestOrdering()
    {
        var measurements = new List<string>
        {
            $"invoker_pool_thread={Thread.CurrentThread.IsThreadPoolThread} " +
            $"invoker_context={SynchronizationContext.Current?.GetType().FullName ?? "none"} " +
            $"processors={Environment.ProcessorCount}; idle means no deliberately occupied workers, " +
            "not an otherwise idle acceptance host. Completion times are observation upper bounds."
        };
        Exception? failure = null;
        var driver = new Thread(() =>
        {
            try
            {
                MeasureIdle(measurements);
                MeasureSaturated(measurements);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        driver.Start();
        var joined = driver.Join(TimeSpan.FromMinutes(2));
        if (joined)
            foreach (var measurement in measurements) output.WriteLine(measurement);
        Assert.True(joined, "Apparatus driver did not finish and restore the pool; no mechanism attributed.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void MeasureIdle(List<string> measurements)
    {
        measurements.Add(PoolState("idle"));
        var asynchronous = new Arm(inline: false);
        var inline = new Arm(inline: true);
        asynchronous.Release();
        asynchronous.Finish("idle asynchronous", measurements);
        inline.Release();
        inline.HarvestInline("idle inline", measurements);
        measurements.Add("idle verdict=neither: no causal attribution from baseline latency alone; " +
                         "completed wrapper harvested by the first Observe, ruling out harvest ordering in this arm.");
    }

    private static void MeasureSaturated(List<string> measurements)
    {
        var asynchronous = new Arm(inline: false);
        var inline = new Arm(inline: true);
        try
        {
            BoundedThreadPoolSaturationApparatus.RunWithBoundedSaturatedThreadPool(evidence =>
            {
                measurements.Add($"saturation={evidence}; {PoolState("saturated")}");
                ThreadPool.GetAvailableThreads(out var before, out _);
                Assert.True(before == 0, "Apparatus did not occupy all workers; no mechanism attributed.");
                asynchronous.Release();

                // This is the original test's measurement window, not a latency pass/fail threshold.
                // While all workers are held, the asynchronous wrapper cannot progress at ANY bound.
                var harvested = SpinWait.SpinUntil(() => asynchronous.Observe(), HistoricalWindow);
                ThreadPool.GetAvailableThreads(out var after, out _);
                measurements.Add($"saturated window: receipt={harvested} wrapper_completed={asynchronous.Wrapper.IsCompleted} " +
                                 $"probe_ran={asynchronous.Probe.IsCompleted} available_workers={after} " +
                                 $"release_elapsed_ms={asynchronous.Elapsed.TotalMilliseconds:F3}");
                Assert.True(after == 0, "Worker availability changed during the controlled window; no mechanism attributed.");
                Assert.True(asynchronous.Source!.Task.IsCompletedSuccessfully,
                    "Runner result was not released; no continuation-latency attribution is possible.");
                Assert.False(asynchronous.Probe.IsCompleted,
                    "Source continuation progressed despite the worker gate; mechanism unresolved.");
                Assert.False(asynchronous.Wrapper.IsCompleted,
                    "Wrapper completed while workers were held; continuation latency was not demonstrated.");
                Assert.False(harvested,
                    "Receipt appeared while workers were held; wall-time unreliability was not demonstrated.");

                inline.Release();
                inline.HarvestInline("saturated inline", measurements);
            });
        }
        catch (Exception ex)
        {
            measurements.Add($"saturated experiment failed; no mechanism attributed: {ex.Message}");
            throw;
        }

        // The apparatus releases blockers and restores both pool settings before either wait.
        asynchronous.Finish("saturated asynchronous after worker release", measurements);
        Assert.True(asynchronous.Probe.Wait(CompletionFailsafe), "Source probe did not run after workers were released.");
        measurements.Add(PoolState("restored"));
        measurements.Add("verdict=thread-pool continuation latency demonstrated: released runner, pending wrapper and " +
                         "pending source probe while workers were held; wrapper completed after worker release. " +
                         "Harvest ordering ruled out: both completed wrappers were harvested on their first Observe. " +
                         "This controlled result does not establish the historical failure's cause.");
    }

    private static string PoolState(string phase)
    {
        ThreadPool.GetMinThreads(out var minimum, out var minIo);
        ThreadPool.GetMaxThreads(out var maximum, out var maxIo);
        ThreadPool.GetAvailableThreads(out var available, out _);
        return $"phase={phase} min_workers={minimum} max_workers={maximum} available_workers={available} " +
               $"min_io={minIo} max_io={maxIo} threads={ThreadPool.ThreadCount} pending={ThreadPool.PendingWorkItemCount}";
    }

    private sealed class Arm
    {
        private readonly AgentOrchestratorKernel _kernel;
        private readonly Goal _goal;
        private readonly ProgressiveReviewGlanceCoordinator _coordinator;
        private readonly RecordingGlanceEvents _events = new();
        private readonly ControlledGlanceReleaseGate? _inline;
        private long _releasedAt;
        private int _observations;

        internal TaskCompletionSource<ProgressiveReviewGlanceDispatchResult>? Source { get; }
        internal Task Probe { get; }
        internal Task<ProgressiveReviewGlanceDispatchResult> Wrapper { get; }
        internal TimeSpan Elapsed => Stopwatch.GetElapsedTime(_releasedAt);

        internal Arm(bool inline)
        {
            var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
            (_kernel, _goal, _) = RunningDeveloperRound(now);
            var runner = new ControlledGlanceRunner();
            if (inline) _inline = runner.EnqueueInlineControl();
            else Source = runner.EnqueuePending();
            var runnerTask = Source?.Task ?? _inline!.Task;
            // A non-disposable probe remains safe even if cleanup delivers a late callback.
            Probe = runnerTask.ContinueWith(_ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _coordinator = NewCoordinator(runner, _events, utcNow: () => now,
                liveChanges: (_, _) => new DispatchLiveChangeSnapshot(["a.cs", "b.cs", "c.cs"], ["a.cs", "b.cs", "c.cs"], 0));
            var started = _coordinator.Observe(_kernel, [_goal]);
            Assert.Single(runner.Calls);
            Assert.Contains(started.ProgressLines, line => line.Contains("result=started", StringComparison.Ordinal));
            Wrapper = TrackedWrapper(_coordinator);
            Assert.NotSame(runnerTask, Wrapper);
            Assert.False(Wrapper.IsCompleted, "Wrapper must be pending before the controlled release.");
        }

        internal void Release()
        {
            var result = new ProgressiveReviewGlanceDispatchResult(ProgressiveReviewGlanceVerdict.OnTrack, "ok", "diff aligns", 10, 5);
            _releasedAt = Stopwatch.GetTimestamp();
            if (Source is not null) Source.SetResult(result);
            else _inline!.SetResult(result);
        }

        internal bool Observe()
        {
            _observations++;
            _ = _coordinator.Observe(_kernel, [_goal]);
            return _events.Receipts.Count == 1;
        }

        internal void Finish(string phase, List<string> measurements)
        {
            Assert.True(Wrapper.Wait(CompletionFailsafe), $"{phase}: wrapper completion event did not occur; mechanism unresolved.");
            var wrapperObservedAt = Elapsed;
            Assert.Empty(_events.Receipts);
            Assert.True(Observe(), $"{phase}: completed wrapper was not harvested by the next Observe; possible coordinator defect.");
            Record(phase, wrapperObservedAt, measurements);
        }

        internal void HarvestInline(string phase, List<string> measurements)
        {
            Assert.True(Wrapper.IsCompletedSuccessfully,
                $"{phase}: inline completion stack returned before wrapper completion; no harvest-ordering attribution possible.");
            var wrapperObservedAt = Elapsed;
            Assert.Empty(_events.Receipts);
            Assert.True(Observe(), $"{phase}: already-completed wrapper was not harvested on the first Observe; possible coordinator defect.");
            Record(phase, wrapperObservedAt, measurements);
        }

        private void Record(string phase, TimeSpan wrapperObservedAt, List<string> measurements)
        {
            var receiptAt = Elapsed;
            measurements.Add($"phase={phase} wrapper_observed_ms={wrapperObservedAt.TotalMilliseconds:F3} " +
                             $"receipt_ms={receiptAt.TotalMilliseconds:F3} exceeds_two_seconds={receiptAt > HistoricalWindow} " +
                             $"observe_calls={_observations} wrapper_completed={Wrapper.IsCompletedSuccessfully}");
            var receipt = Assert.Single(_events.Receipts);
            Assert.Equal("ChangedFiles", receipt.Trigger);
            Assert.Equal("OnTrack", receipt.Verdict);
            Assert.Equal(15, receipt.TotalTokens);
        }
    }

    private static Task<ProgressiveReviewGlanceDispatchResult> TrackedWrapper(ProgressiveReviewGlanceCoordinator coordinator)
    {
        // Passive diagnostic probe of the actual tracked task. Reading the two fields keeps wrapper
        // completion distinct from runner completion and receipt harvest, without invoking private
        // coordinator behavior or adding product instrumentation. Fail loudly if its storage changes.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var runningField = typeof(ProgressiveReviewGlanceCoordinator).GetField("_running", fields)
            ?? throw new InvalidOperationException("Tracked-wrapper probe cannot find coordinator running storage.");
        var running = Assert.Single(((IEnumerable)runningField.GetValue(coordinator)!).Cast<object>());
        var taskField = running.GetType().GetField("<Task>k__BackingField", fields)
            ?? throw new InvalidOperationException("Tracked-wrapper probe cannot find running task storage.");
        return Assert.IsAssignableFrom<Task<ProgressiveReviewGlanceDispatchResult>>(taskField.GetValue(running));
    }
}
