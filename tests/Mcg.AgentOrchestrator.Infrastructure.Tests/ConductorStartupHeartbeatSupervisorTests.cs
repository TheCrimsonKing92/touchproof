using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: scripted processes, per-test signals and a unique artifact directory.
public sealed class ConductorStartupHeartbeatSupervisorTests
{
    private const string ReadyLine = "LOOP_READY lock=acquired";
    private const string StateLoadHeartbeat = "STARTUP_PROGRESS phase=state-load elapsed_ms=0";

    [Xunit.Theory]
    [Xunit.InlineData(ReadyLine)]
    [Xunit.InlineData(StateLoadHeartbeat)]
    public async Task StartupHeartbeat_RearmsSilenceWindow_AdoptsWithoutRestore(string firstLine)
    {
        await using var scenario = new Scenario(restoring: false, firstLine);
        var beforeReadiness = firstLine == StateLoadHeartbeat;
        var firstDeadline = await scenario.NextDeadlineAsync(beforeReadiness);
        var heartbeat = ConductorStartupHeartbeat.Format(ConductorStartupHeartbeat.Phases.TerminalSweep,
            TimeSpan.FromSeconds(15));
        Assert.Equal("STARTUP_PROGRESS phase=terminal-sweep elapsed_ms=15000", heartbeat);
        scenario.Emit(heartbeat);
        firstDeadline.Release.TrySetResult();
        await scenario.NextDeadlineAsync(beforeReadiness);
        Assert.Equal(2, scenario.Host.Requests.Count);
        Assert.DoesNotContain(scenario.Events.Events, evt => evt.Status == "reverted");
        if (beforeReadiness)
        {
            scenario.Emit(ReadyLine);
            await scenario.NextDeadlineAsync(beforeReadiness: false);
        }
        scenario.Emit("LOOP_START tick=0");
        for (var tick = 1; tick <= 3; tick++)
            scenario.Emit($"TICK_END tick={tick} activation=true");
        await scenario.WaitForAsync(scenario.Activated.Task, "activation event");
        scenario.StopActive();

        Assert.Equal(0, await scenario.WaitForRunAsync());
        Assert.Equal(2, scenario.Host.Requests.Count);
        Assert.Single(scenario.Events.Events.Where(evt =>
            evt.Operation == "activation" && evt.Status == "adopted"));
        Assert.DoesNotContain(scenario.Events.Events, evt =>
            evt.Operation == "activation" && (evt.Status == "reverted" || evt.Status == "failed-both"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(ReadyLine)]
    [Xunit.InlineData(StateLoadHeartbeat)]
    public async Task SilentStartup_SilenceDeadline_RevertsAndLaunchesRestore(string firstLine)
    {
        await using var scenario = new Scenario(restoring: false, firstLine);
        var deadline = await scenario.NextDeadlineAsync(beforeReadiness: firstLine == StateLoadHeartbeat);
        deadline.Release.TrySetResult();

        Assert.Equal(0, await scenario.WaitForRunAsync());
        Assert.Equal(3, scenario.Host.Requests.Count);
        Assert.Null(scenario.Host.Requests[2].CommandPrefix);
        var reverted = Assert.Single(scenario.Events.Events.Where(evt =>
            evt.Operation == "activation" && evt.Status == "reverted"));
        Assert.Contains("reason=ReadinessNeverReported", reverted.Detail, StringComparison.Ordinal);
    }

    private sealed class ScriptedDeadline
    {
        public TaskCompletionSource Release { get; } = NewSignal();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Scenario : IAsyncDisposable
    {
        internal static readonly TimeSpan SilenceWindow = TimeSpan.FromSeconds(30);
        private readonly Channel<ScriptedDeadline> _readinessDeadlines = Channel.CreateUnbounded<ScriptedDeadline>();
        private readonly Channel<ScriptedDeadline> _activationDeadlines = Channel.CreateUnbounded<ScriptedDeadline>();
        private readonly CancellationTokenSource _runCts =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly TaskCompletionSource<ConductorSupervisorProcessResult> _activeExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _outputDirectory =
            Path.Combine(Path.GetTempPath(), $"mcg-startup-progress-{Guid.NewGuid():N}");
        private readonly MemoryStream _lease = new();

        public ScriptedHost Host { get; }
        public RecordingRunEventStore Events { get; } = new();
        public TaskCompletionSource Activated { get; } = NewSignal();
        public ConductorSupervisorProcessRequest? ActiveRequest { get; private set; }
        public Task<int> Run { get; }

        public Scenario(bool restoring, string initialLine)
        {
            Events.OnAppend = evt =>
            {
                if (evt.Operation == "activation" && evt.Status == (restoring ? "restored" : "adopted"))
                    Activated.TrySetResult();
            };
            Host = new ScriptedHost((request, index, cancellationToken) =>
            {
                if (index == 0)
                {
                    ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                        new ConductorContinuityExitArtifact("max-duration", 1, 1, true));
                    return Task.FromResult(new ConductorSupervisorProcessResult(0, 800));
                }
                if (restoring && index == 1)
                    return Task.FromResult(new ConductorSupervisorProcessResult(-1, 801));
                if (index == (restoring ? 2 : 1))
                {
                    ActiveRequest = request;
                    request.OnStandardOutputLine!(initialLine);
                    cancellationToken.Register(() => _activeExit.TrySetResult(
                        new ConductorSupervisorProcessResult(-1, 802, TerminationConfirmed: true)));
                    return _activeExit.Task;
                }
                Assert.Equal(2, index);
                Assert.Null(request.CommandPrefix);
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath,
                    new ConductorContinuityExitArtifact("stop-file", 1, 0, false));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 803));
            });
            var prepared = new ConductorPreparedSuccessor(
                "C:\\startup-successor", "C:\\startup-successor\\Mcg.AgentOrchestrator.App.dll",
                "head-startup", "head-startup", "LOOP_START selfCheck=true", _lease);
            var supervisor = new ConductorContinuitySupervisor(Host, Events,
                inspectStores: () => [new("fixture-store", "fixture.db", 1, 1)], stageSuccessor: _ => prepared, activationHealthyTicks: 3, readinessTimeout: SilenceWindow,
                delay: (_, _) => RecordDeadline(_readinessDeadlines.Writer),
                activationDelay: (_, _) => RecordDeadline(_activationDeadlines.Writer),
                verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"));
            Run = supervisor.RunAsync(["conduct", "--loop"], "C:\\repo", _outputDirectory,
                "default", "default", cancellationToken: _runCts.Token);
        }

        public void Emit(string line) => ActiveRequest!.OnStandardOutputLine!(line);

        private static Task RecordDeadline(ChannelWriter<ScriptedDeadline> writer)
        {
            var deadline = new ScriptedDeadline();
            if (!writer.TryWrite(deadline))
                throw new InvalidOperationException("Could not record startup delay");
            return deadline.Release.Task;
        }

        public async Task<ScriptedDeadline> NextDeadlineAsync(bool beforeReadiness)
        {
            var deadlines = beforeReadiness ? _readinessDeadlines : _activationDeadlines;
            var next = deadlines.Reader.ReadAsync(_runCts.Token).AsTask();
            await WaitForAsync(next, beforeReadiness ? "readiness deadline" : "activation deadline");
            return await next;
        }

        public async Task WaitForAsync(Task signal, string eventName)
        {
            Task completed;
            try
            {
                completed = await Task.WhenAny(signal, Run)
                    .WaitAsync(TimeSpan.FromSeconds(30), _runCts.Token);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"Timed out waiting for {eventName}", exception);
            }
            Assert.True(completed == signal, $"Supervisor ended before {eventName}");
            await signal;
        }

        public async Task<int> WaitForRunAsync()
        {
            try { return await Run.WaitAsync(TimeSpan.FromSeconds(30), _runCts.Token); }
            catch (TimeoutException exception)
            {
                throw new TimeoutException("Timed out waiting for supervisor completion", exception);
            }
        }

        public void StopActive()
        {
            ConductorContinuityExitArtifact.Write(ActiveRequest!.ExitArtifactPath,
                new ConductorContinuityExitArtifact("stop-file", 3, 0, false));
            _activeExit.TrySetResult(new ConductorSupervisorProcessResult(0, 802));
        }

        public async ValueTask DisposeAsync()
        {
            _runCts.Cancel();
            _activeExit.TrySetResult(new ConductorSupervisorProcessResult(-1, 802));
            try { await Run; }
            catch (OperationCanceledException) { }
            finally
            {
                _lease.Dispose();
                _runCts.Dispose();
                if (Directory.Exists(_outputDirectory)) Directory.Delete(_outputDirectory, recursive: true);
            }
        }
    }

    private sealed class ScriptedHost(
        Func<ConductorSupervisorProcessRequest, int, CancellationToken, Task<ConductorSupervisorProcessResult>> step)
        : IConductorSupervisorProcessHost
    {
        public List<ConductorSupervisorProcessRequest> Requests { get; } = [];

        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(request);
            return step(request, index, cancellationToken);
        }
    }

    private sealed class RecordingRunEventStore : IRunEventStore
    {
        public List<RunEventAppend> Events { get; } = [];
        public Action<RunEventAppend>? OnAppend { get; set; }

        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            OnAppend?.Invoke(evt);
            return Task.FromResult(new RunEventRecord(Events.Count, evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId, evt.Operation,
                evt.Status, evt.Detail, evt.PayloadJson));
        }

        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0,
            string? goalId = null, int maxCount = 500, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
