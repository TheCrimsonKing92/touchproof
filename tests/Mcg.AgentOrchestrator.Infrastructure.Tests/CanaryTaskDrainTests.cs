using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CanaryTaskDrainTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    private const string LandingSha = "landing-under-test";
    private const string EnginePath = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/TestCoverageInvariant.cs";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    [Xunit.Fact]
    public async Task ExpiredWaitReportsSnapshotOnce()
    {
        var clock = new ManualStewardTimeProvider();
        var progress = new ConcurrentQueue<string>();
        var drain = new CanaryTaskDrain(Bound, Grace, clock, progress.Enqueue);
        var firstWork = NewSignal();
        var secondWork = NewSignal();
        Task[] snapshot = [firstWork.Task, secondWork.Task, Task.CompletedTask];

        try
        {
            var firstWait = Task.Run(() => drain.Wait(snapshot));
            await clock.TimerCreated.Task.WaitAsync(Watchdog);
            clock.Advance(Bound);
            Assert.False(await firstWait.WaitAsync(Watchdog));
            Assert.Equal("CANARY_GATE result=drain-bound-hit pending=2 boundSeconds=2.5", Assert.Single(progress));

            firstWork.SetResult();
            // test-design-discipline: allow-negative-wait - The manual clock already expired the shared deadline, and the watchdog throws if the wait fails to return.
            Assert.False(await Task.Run(() => drain.Wait(snapshot)).WaitAsync(Watchdog));
            Assert.Equal(2, progress.Count);
            Assert.Equal("CANARY_GATE result=drain-bound-hit pending=1 boundSeconds=2.5", progress.Last());
        }
        finally
        {
            firstWork.TrySetResult();
            secondWork.TrySetResult();
        }
    }

    [Xunit.Fact]
    public async Task RequestedStopPreservesReason()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        var cancelled = NewSignal();
        var release = NewSignal();
        var terminated = NewSignal();
        var coordinator = fixture.Coordinator(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.SetResult();
            await release.Task;
            terminated.SetResult();
            token.ThrowIfCancellationRequested();
            return PostLandingCanaryOutcome.Passed(1, "finished before cancellation");
        });
        var landing = coordinator.LaunchLandingAsync(Receipt());
        Task<BatchLoopSummary>? loop = null;
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            loop = fixture.StopLoop(coordinator);
            await fixture.Clock.TimerCreated.Task.WaitAsync(Watchdog);
            Assert.False(cancelled.Task.IsCompleted);
            fixture.Clock.Advance(Bound);
            await cancelled.Task.WaitAsync(Watchdog);
            Assert.False(loop.IsCompleted);

            release.SetResult();
            var summary = await loop.WaitAsync(Watchdog);
            Assert.True(terminated.Task.IsCompletedSuccessfully);
            Assert.True(summary.StopRequested);
            Assert.Equal("stop-file", summary.StopReason);
            Assert.Contains(fixture.Progress, line => line.Contains("result=drain-bound-hit", StringComparison.Ordinal));
            Assert.DoesNotContain(fixture.Progress, IsTerminationExpiry);
            Assert.Equal(PostLandingCanaryDisposition.Deferred, await landing.WaitAsync(Watchdog));
            Assert.Null(await fixture.Events.FindTerminalAsync(LandingSha));
        }
        finally
        {
            release.TrySetResult();
            await landing.WaitAsync(Watchdog);
            if (loop is not null) await loop.WaitAsync(Watchdog);
        }
    }

    [Xunit.Fact]
    public async Task TerminationGraceExpiresOnce()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(started: false);
        var entered = NewSignal();
        var cancelled = NewSignal();
        var release = NewSignal();
        var coordinator = fixture.Coordinator(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.SetResult();
            await release.Task;
            return PostLandingCanaryOutcome.Passed(1, "runner exited after grace");
        });
        Task[] recovery = [];
        Task<BatchLoopSummary>? loop = null;
        try
        {
            await coordinator.ResumePending().WaitAsync(Watchdog);
            recovery = coordinator.Execution.Snapshot();
            await entered.Task.WaitAsync(Watchdog);
            loop = fixture.StopLoop(coordinator);
            await fixture.Clock.TimerCreated.Task.WaitAsync(Watchdog);
            fixture.Clock.Advance(Bound);
            await cancelled.Task.WaitAsync(Watchdog);
            Assert.False(loop.IsCompleted);
            fixture.Clock.Advance(Grace);

            var summary = await loop.WaitAsync(Watchdog);
            Assert.True(summary.StopRequested);
            Assert.Equal("stop-file", summary.StopReason);
            Assert.Contains(recovery, task => !task.IsCompleted);
            Assert.Equal(2, fixture.Progress.Count(line => line.Contains("result=drain-bound-hit", StringComparison.Ordinal)));
            Assert.Equal($"CANARY_GATE result=termination-grace-expired runners={LandingSha} graceSeconds=5",
                Assert.Single(fixture.Progress.Where(IsTerminationExpiry)));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(recovery).WaitAsync(Watchdog);
            if (loop is not null) await loop.WaitAsync(Watchdog);
        }

        Assert.Collection(await fixture.Events.ReadForLandingAsync(LandingSha),
            queued => Assert.Equal(PostLandingCanaryEventKind.Queued, queued.Kind),
            started => Assert.Equal(PostLandingCanaryEventKind.Started, started.Kind));
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task OutputDrainsShareTerminationGrace(bool holdStdout)
    {
        var clock = new ManualStewardTimeProvider();
        var execution = new CanaryExecution();
        var drain = new CanaryTaskDrain(Bound, Grace, clock, _ => { }, execution);
        var heldOutput = NewSignal();
        var terminationRequested = NewSignal();
        var outputCancelled = NewSignal();
        var runner = execution.RegisterRunner(LandingSha);
        var process = PostLandingCanaryRunner.AwaitProcessCompletionAsync(
            Task.CompletedTask,
            holdStdout ? heldOutput.Task : Task.CompletedTask,
            holdStdout ? Task.CompletedTask : heldOutput.Task,
            execution.Token,
            () => terminationRequested.SetResult(),
            runner,
            () => outputCancelled.SetResult());
        var wait = Task.Run(() => drain.Wait([process]));
        try
        {
            await clock.TimerCreated.Task.WaitAsync(Watchdog);
            clock.Advance(Bound);
            await terminationRequested.Task.WaitAsync(Watchdog);
            Assert.False(outputCancelled.Task.IsCompleted);
            Assert.False(process.IsCompleted);
            clock.Advance(Grace);

            Assert.False(await wait.WaitAsync(Watchdog));
            await outputCancelled.Task.WaitAsync(Watchdog);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WaitAsync(Watchdog));
            Assert.False(heldOutput.Task.IsCompleted);
            Assert.True(runner.GraceExpired);
        }
        finally
        {
            heldOutput.TrySetResult();
            runner.Dispose();
            execution.Retire();
        }
    }

    [Xunit.Fact]
    public async Task EarlyCompletionIsSilent()
    {
        using var fixture = new Fixture();
        var entered = NewSignal();
        var release = NewSignal();
        var coordinator = fixture.Coordinator(async (_, token) =>
        {
            entered.SetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
            return PostLandingCanaryOutcome.Passed(1, "completed within the drain budget");
        });
        var landing = coordinator.LaunchLandingAsync(Receipt());
        Task<BatchLoopSummary>? loop = null;
        try
        {
            await entered.Task.WaitAsync(Watchdog);
            loop = fixture.StopLoop(coordinator);
            await fixture.Clock.TimerCreated.Task.WaitAsync(Watchdog);
            release.SetResult();

            Assert.Equal("stop-file", (await loop.WaitAsync(Watchdog)).StopReason);
            Assert.Equal(PostLandingCanaryDisposition.Passed, await landing.WaitAsync(Watchdog));
            Assert.DoesNotContain(fixture.Progress, line => line.Contains("result=drain-bound-hit", StringComparison.Ordinal));
            Assert.DoesNotContain(fixture.Progress, IsTerminationExpiry);
        }
        finally
        {
            release.TrySetResult();
            await landing.WaitAsync(Watchdog);
            if (loop is not null) await loop.WaitAsync(Watchdog);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SuccessorResumesNonterminalLanding(bool started)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync(started);
        var initial = await fixture.Events.ReadForLandingAsync(LandingSha);
        Assert.Equal(started ? PostLandingCanaryEventKind.Started : PostLandingCanaryEventKind.Queued, initial.Last().Kind);
        Assert.DoesNotContain(initial, item => item.IsTerminal);

        var entered = new ConcurrentQueue<string>();
        var successor = fixture.Coordinator(async (request, token) =>
        {
            var events = await fixture.Events.ReadForLandingAsync(request.LandingSha, token);
            entered.Enqueue(events.Last(item => item.Kind == PostLandingCanaryEventKind.Started).EventId);
            return PostLandingCanaryOutcome.Passed(1, "resumed from store");
        });
        await successor.ResumePending().WaitAsync(Watchdog);
        await Task.WhenAll(successor.Execution.Snapshot()).WaitAsync(Watchdog);

        Assert.Equal(PostLandingCanaryEventIds.Started(LandingSha, started ? 2 : 1), Assert.Single(entered));
        var receipt = Assert.Single((await fixture.Events.ReadForLandingAsync(LandingSha)).Where(item => item.IsTerminal));
        Assert.Equal(PostLandingCanaryEventKind.Passed, receipt.Kind);
        Assert.Equal("resumed from store", receipt.Payload.Detail);
    }

    [Xunit.Fact]
    public async Task PostExpiryLaunchThenSuccessorRunsOncePerOrdinal()
    {
        using var fixture = new Fixture();
        var entries = new ConcurrentQueue<(string Coordinator, string EventId)>();
        Func<PostLandingCanaryRequest, CancellationToken, Task<PostLandingCanaryOutcome>> Runner(string coordinator) =>
            async (request, token) =>
            {
                var events = await fixture.Events.ReadForLandingAsync(request.LandingSha, token);
                entries.Enqueue((coordinator, events.Last(item => item.Kind == PostLandingCanaryEventKind.Started).EventId));
                return PostLandingCanaryOutcome.Passed(1, coordinator);
            };

        var exiting = fixture.Coordinator(Runner("exiting"));
        var execution = exiting.Execution;
        var drain = new CanaryTaskDrain(Bound, Grace, fixture.Clock, fixture.Progress.Enqueue, execution);
        var unrelated = NewSignal();
        try
        {
            var wait = Task.Run(() => drain.Wait([unrelated.Task]));
            await fixture.Clock.TimerCreated.Task.WaitAsync(Watchdog);
            fixture.Clock.Advance(Bound);
            Assert.False(await wait.WaitAsync(Watchdog));
            Assert.True(execution.Token.IsCancellationRequested);

            Assert.Equal(PostLandingCanaryDisposition.Deferred, await exiting.LaunchLandingAsync(Receipt()).WaitAsync(Watchdog));
            var queued = Assert.Single(await fixture.Events.ReadForLandingAsync(LandingSha));
            Assert.Equal(PostLandingCanaryEventIds.Queued(LandingSha), queued.EventId);
            Assert.Empty(entries);

            var successor = fixture.Coordinator(Runner("successor"));
            await successor.ResumePending().WaitAsync(Watchdog);
            await Task.WhenAll(successor.Execution.Snapshot()).WaitAsync(Watchdog);

            var events = await fixture.Events.ReadForLandingAsync(LandingSha);
            var ordinals = events.Where(item => item.Kind == PostLandingCanaryEventKind.Started).Select(item => item.EventId);
            var ordinal = Assert.Single(ordinals);
            Assert.Equal(PostLandingCanaryEventIds.Started(LandingSha, 1), ordinal);
            Assert.Equal(("successor", ordinal), Assert.Single(entries));
            var terminal = Assert.Single(events.Where(item => item.IsTerminal));
            Assert.Equal(PostLandingCanaryEventKind.Passed, terminal.Kind);
            Assert.Equal("successor", terminal.Payload.Detail);
        }
        finally
        {
            unrelated.TrySetResult();
            exiting.RetireExecution(execution);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ConductorLandingReceipt Receipt() => new("engine-goal", [EnginePath], LandingSha);

    private static bool IsTerminationExpiry(string line) =>
        line.StartsWith("CANARY_GATE result=termination-grace-expired ", StringComparison.Ordinal);

    // Each test uses its own SQLite store, canary lease, stop file and progress sink.
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory("mcg-canary-drain");
        private string EventPath => Path.Combine(_root, "run-events.db");
        internal ManualStewardTimeProvider Clock { get; } = new();
        internal ConcurrentQueue<string> Progress { get; } = new();
        internal PostLandingCanaryEventStore Events => new(new SqliteRunEventStore(EventPath), EventPath);

        internal PostLandingCanaryCoordinator Coordinator(
            Func<PostLandingCanaryRequest, CancellationToken, Task<PostLandingCanaryOutcome>> run)
        {
            var events = Events;
            var runner = new PostLandingCanaryRunner(_root, runOverride: run, buildCacheRoot: Path.Combine(_root, "build"));
            return new(PostLandingCanaryConfiguration.Default, runner,
                events, new AcceptanceEngineCircuitBreaker(events), progress: Progress.Enqueue);
        }

        internal async Task SeedAsync(bool started)
        {
            var events = Events;
            var now = DateTimeOffset.UtcNow;
            var payload = new PostLandingCanaryEventPayload(PostLandingCanaryEventPayload.CanaryTag,
                LandingSha, [EnginePath], null, 0, "pending recovery", null, null);
            await events.AppendOnceAsync(PostLandingCanaryEventKind.Queued, payload,
                PostLandingCanaryEventIds.Queued(LandingSha), now);
            if (started)
            {
                await events.AppendOnceAsync(PostLandingCanaryEventKind.Started, payload with { StartedAt = now, AttemptCount = 1 },
                    PostLandingCanaryEventIds.Started(LandingSha, 1), now);
            }
        }

        internal Task<BatchLoopSummary> StopLoop(PostLandingCanaryCoordinator coordinator)
        {
            var stopPath = Path.Combine(_root, "stop");
            File.WriteAllText(stopPath, "stop");
            var loop = new ConductorBatchLoop(postLandingCanary: coordinator, canaryDrainBound: Bound,
                canaryTerminationGrace: Grace, canaryDrainClock: Clock);
            return Task.Run(() => loop.Run(new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative, stopPath));
        }

        public void Dispose() => TryDeleteDirectory(_root);
    }
}
