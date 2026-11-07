using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique project/state roots, fake process host, injected clock and process probes.
public sealed class ProjectFleetRollingRestartDrainTests
{
    [Fact]
    public async Task LiveGateDelaysStopUntilDrainThenReleasesHold()
    {
        using var fixture = new Harness();
        var project = fixture.Project("one");
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Delay = async (duration, token) =>
        {
            waiting.TrySetResult();
            await drained.Task.WaitAsync(token);
            fixture.Clock.Advance(duration);
        };
        var run = fixture.Restart(p =>
        {
            fixture.Acknowledge(p, drained.Task.IsCompleted ? 0 : 1);
            return !drained.Task.IsCompleted;
        }).RunAsync([project], Harness.Target, default);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang detector: drain poll was not reached.
        Assert.True(File.Exists(fixture.Marker(project)));
        Assert.False(File.Exists(project.ResolveWorkspace().ConductorStopFilePath));
        Assert.Empty(fixture.Host.Requests);
        drained.SetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        var outcome = Assert.Single(result.Projects);
        Assert.Equal("restarted", outcome.State);
        Assert.Equal(Harness.Target, outcome.BuildCommit);
        Assert.Single(fixture.Host.Requests);
        Assert.False(File.Exists(fixture.Marker(project)));
        Assert.False(File.Exists(project.ResolveWorkspace().ConductorStopFilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainTimeoutOrProbeFailureNeverWritesStop(bool throws)
    {
        using var fixture = new Harness();
        var project = fixture.Project("one");
        var result = await fixture.Restart(_ => throws ? throw new IOException("unreadable heartbeat") : true)
            .RunAsync([project], Harness.Target, default);
        var outcome = Assert.Single(result.Projects);
        Assert.Equal("failed-drain-timeout", outcome.State);
        Assert.Equal("old-one", outcome.BuildCommit);
        if (throws) Assert.Contains("unreadable heartbeat", outcome.Error);
        Assert.Empty(fixture.Host.Requests);
        Assert.False(File.Exists(project.ResolveWorkspace().ConductorStopFilePath));
        Assert.False(File.Exists(fixture.Marker(project)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task ClearHeartbeatRequiresAcknowledgedZeroActiveAttempts(int active)
    {
        using var fixture = new Harness();
        var project = fixture.Project("one");
        var result = await fixture.Restart(p =>
        {
            if (active >= 0) fixture.Acknowledge(p, active);
            return false;
        }).RunAsync([project], Harness.Target, default);
        Assert.Equal("failed-drain-timeout", Assert.Single(result.Projects).State);
        Assert.False(File.Exists(project.ResolveWorkspace().ConductorStopFilePath));
        Assert.Empty(fixture.Host.Requests);
    }

    [Theory]
    [InlineData("stop-timeout", "failed-stop-timeout")]
    [InlineData("start-failure", "failed-start")]
    [InlineData("operator-stop", "failed-stop")]
    [InlineData("stop-write-failure", "failed-stop")]
    [InlineData("cancel", "cancel")]
    public async Task EveryExceptionalOutcomeReleasesOnlyItsHold(string mode, string expected)
    {
        using var fixture = new Harness();
        using var cancellation = new CancellationTokenSource();
        var project = fixture.Project("one");
        if (mode == "stop-timeout") fixture.ExitOnStop = false;
        if (mode == "start-failure") fixture.Host.FailingProject = "one";
        if (mode == "stop-write-failure") Directory.CreateDirectory(project.ResolveWorkspace().ConductorStopFilePath);
        if (mode == "cancel") fixture.Delay = (_, _) => { cancellation.Cancel(); return Task.CompletedTask; };
        var run = fixture.Restart(p =>
        {
            fixture.Acknowledge(p, 0);
            // Arrive during drain so the initial lock probe still observes a running conductor.
            if (mode == "operator-stop") File.WriteAllText(p.ResolveWorkspace().ConductorStopFilePath, "operator");
            return mode == "cancel";
        }).RunAsync([project], Harness.Target, cancellation.Token);
        if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        else
        {
            var outcome = Assert.Single((await run).Projects);
            Assert.Equal(expected, outcome.State);
            Assert.Equal("old-one", outcome.BuildCommit);
        }
        Assert.False(File.Exists(fixture.Marker(project)));
        Assert.Equal(mode == "operator-stop", File.Exists(project.ResolveWorkspace().ConductorStopFilePath));
        if (mode == "operator-stop")
        {
            Assert.Equal("operator", File.ReadAllText(project.ResolveWorkspace().ConductorStopFilePath));
            Assert.Empty(fixture.Host.Requests);
        }
    }

    [Fact]
    public async Task ForeignHoldIsPreservedAndPreventsStop()
    {
        using var fixture = new Harness();
        var project = fixture.Project("one");
        var directory = project.ResolveWorkspace().OrchestratorDirectory;
        var foreign = FleetRestartAdmissionHold.Acquire(directory, Harness.Target, fixture.Clock.GetUtcNow().AddHours(1));
        var result = await fixture.Restart(_ => false).RunAsync([project], Harness.Target, default);
        Assert.Equal("failed-drain-hold", Assert.Single(result.Projects).State);
        Assert.Equal(foreign, JsonSerializer.Deserialize<FleetRestartAdmissionHold>(File.ReadAllText(fixture.Marker(project))));
        Assert.False(File.Exists(project.ResolveWorkspace().ConductorStopFilePath));
        foreign.Release(directory);
    }

    [Theory]
    [InlineData(true, "deadline-expired")]
    [InlineData(false, "owner-exited")]
    public void AbandonedHoldIsRemovedAndReportedOnce(bool expired, string reason)
    {
        using var fixture = new Harness();
        var project = fixture.Project("one");
        var directory = project.ResolveWorkspace().OrchestratorDirectory;
        FleetRestartAdmissionHold.Acquire(directory, Harness.Target,
            fixture.Clock.GetUtcNow() + (expired ? TimeSpan.Zero : TimeSpan.FromHours(1)));
        var lines = new List<string>();
        Assert.Null(FleetRestartAdmissionHold.ReadForAdmission(directory, fixture.Clock.GetUtcNow(), lines.Add,
            new OwnerProbe(false)));
        Assert.Null(FleetRestartAdmissionHold.ReadForAdmission(directory, fixture.Clock.GetUtcNow(), lines.Add,
            new OwnerProbe(false)));
        Assert.Contains("reason=" + reason, Assert.Single(lines));
        Assert.False(File.Exists(fixture.Marker(project)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void Admission_InvalidMarker_HoldsAndReportsReadFailure(string marker)
    {
        using var fixture = new Harness();
        var project = fixture.Project("one");
        var directory = project.ResolveWorkspace().OrchestratorDirectory;
        File.WriteAllText(fixture.Marker(project), marker);
        var lines = new List<string>();

        var admission = new ConductorAcceptanceAdmissionHold(directory, fixture.Clock.GetUtcNow,
            lines.Add, () => 0, 1);

        Assert.True(admission.Suppress(deferringStop: false, pendingSelfRelaunch: false));
        Assert.True(admission.FleetHeld());
        admission.Acknowledge();
        Assert.Single(lines, line => line.StartsWith("FLEET_RESTART_HOLD_READ_FAILED reason="));
        Assert.Contains("ADMISSION tick=1 result=held reason=fleet-restart-drain", lines);
        Assert.Equal(marker, File.ReadAllText(fixture.Marker(project)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void Acknowledge_InvalidatedMarker_ReportsFailureAndKeepsAdmissionHeld(string marker)
    {
        using var fixture = new Harness();
        var project = fixture.Project("one");
        var directory = project.ResolveWorkspace().OrchestratorDirectory;
        FleetRestartAdmissionHold.Acquire(directory, Harness.Target, fixture.Clock.GetUtcNow().AddHours(1));
        var lines = new List<string>();
        var admission = new ConductorAcceptanceAdmissionHold(directory, fixture.Clock.GetUtcNow,
            lines.Add, () => 0, 1);
        File.WriteAllText(fixture.Marker(project), marker);

        admission.Acknowledge();

        Assert.Single(lines, line => line.StartsWith("FLEET_RESTART_HOLD_ACK_FAILED reason="));
        Assert.True(admission.Suppress(deferringStop: false, pendingSelfRelaunch: false));
        Assert.Single(lines, line => line.StartsWith("FLEET_RESTART_HOLD_READ_FAILED reason="));
        Assert.Equal(marker, File.ReadAllText(fixture.Marker(project)));
    }

    [Fact]
    public void HeartbeatProbeFiltersOwnersAndFailsClosed()
    {
        using var fixture = new Harness();
        var directory = fixture.Project("one").ResolveWorkspace().OrchestratorDirectory;
        var now = fixture.Clock.GetUtcNow();
        GateHeartbeatStatus Status(string? owner, string state = "running") => new(0, "unused", true, null,
            new(null, "gate", "test", 0, 42, 42, state, now, now, now.AddHours(-1), 0, 0, 0,
                OwnerStateDirectory: owner), null, null);
        bool Live(params GateHeartbeatStatus[] statuses) => FleetRestartAdmissionHold.HasLiveGate(directory, statuses, now);
        Assert.False(Live(new GateHeartbeatStatus(0, "missing", false, "missing", null, null, null)));
        Assert.True(Live(new GateHeartbeatStatus(0, "bad", false, "invalid", null, null, null)));
        Assert.False(Live(Status(Path.Combine(directory, "foreign"))));
        Assert.False(Live(Status(directory, "completed")));
        Assert.True(Live(Status(directory))); // Stalled is still in flight.
        Assert.True(Live(Status(null)));
        Assert.True(Live(Status(Path.Combine(directory, "foreign")), Status(directory.ToUpperInvariant())));
    }

    internal sealed class Harness : IDisposable
    {
        internal const string Target = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly ProjectFleetSupervisorTests.Fixture _files = new();
        private readonly Probe _probe;
        internal Host Host { get; } = new();
        internal ManualClock Clock { get; } = new();
        internal bool ExitOnStop { get; set; } = true;
        internal Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }
        internal Harness() => _probe = new Probe(this);
        internal OrchestratorProject Project(string name)
        {
            var project = _files.Project(name);
            var pid = _probe.Projects.Count + 1;
            _probe.Projects[pid] = project;
            _files.WriteLock(project, $"{pid}\n{Clock.GetUtcNow():O}\n{Clock.GetUtcNow():O}\n");
            return project;
        }
        internal string Marker(OrchestratorProject project) => FleetRestartAdmissionHold.MarkerPath(project.ResolveWorkspace().OrchestratorDirectory);
        internal void Acknowledge(OrchestratorProject project, int count)
        {
            var hold = JsonSerializer.Deserialize<FleetRestartAdmissionHold>(File.ReadAllText(Marker(project)))!;
            hold.Acknowledge(project.ResolveWorkspace().OrchestratorDirectory, count);
        }
        internal ProjectFleetRollingRestart Restart(Func<OrchestratorProject, bool>? probe = null) =>
            new(new ProjectFleetSupervisor(Host, new ProjectEnvironmentResolver(
                    () => new Dictionary<string, string> { ["JAVA_HOME"] = "jdk" }, () => new Dictionary<string, string>()),
                    _probe, () => Target), p => "old-" + p.Name, Clock, _probe,
                stopTimeout: TimeSpan.FromSeconds(1), delay: (duration, token) =>
                {
                    if (Delay is not null) return Delay(duration, token);
                    token.ThrowIfCancellationRequested();
                    Clock.Advance(duration);
                    return Task.CompletedTask;
                }, liveGateProbe: probe, drainTimeout: TimeSpan.FromSeconds(1));
        public void Dispose() => _files.Dispose();
        private sealed class Probe(Harness fixture) : IConductLockPidProbe
        {
            internal Dictionary<int, OrchestratorProject> Projects { get; } = [];
            private readonly HashSet<int> _exited = [];
            public bool IsRunning(int pid) => throw new InvalidOperationException("Identity probe required.");
            public bool IsSameProcess(int pid, DateTimeOffset started)
            {
                if (fixture.ExitOnStop && File.Exists(Projects[pid].ResolveWorkspace().ConductorStopFilePath)) _exited.Add(pid);
                return !_exited.Contains(pid);
            }
        }
    }

    internal sealed class Host : IConductorSupervisorProcessHost
    {
        internal List<ConductorSupervisorProcessRequest> Requests { get; } = [];
        internal string? FailingProject { get; set; }
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (request.Arguments.Contains("--project=" + FailingProject)) throw new IOException("launch denied");
            request.OnProcessStarted!(100 + Requests.Count);
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 100 + Requests.Count));
        }
    }

    internal sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(_ticks);
        internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }

    private sealed class OwnerProbe(bool alive) : IConductLockPidProbe
    {
        public bool IsRunning(int pid) => throw new InvalidOperationException("Identity probe required.");
        public bool IsSameProcess(int pid, DateTimeOffset started) => alive;
    }
}
