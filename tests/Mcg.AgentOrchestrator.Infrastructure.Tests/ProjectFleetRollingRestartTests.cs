using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: private fixture roots, fake hosts, scripted lock identities and a manual clock.
public sealed class ProjectFleetRollingRestartTests
{
    [Fact]
    public async Task Restart_ThreeLiveProjects_StopsAndStartsOneAtATimeInNameOrder()
    {
        using var harness = new Harness();
        var projects = new[] { harness.Project("c"), harness.Project("A"), harness.Project("b") };
        var live = projects.ToDictionary(project => project.Name, _ => true);
        var events = new List<string>();
        var stopped = new HashSet<string>();
        var inFlight = 0;
        var maximum = 0;
        foreach (var (project, index) in projects.Select((project, index) => (project, index)))
            harness.WriteLive(project, index + 1, () =>
            {
                if (File.Exists(project.ResolveWorkspace().ConductorStopFilePath) && stopped.Add(project.Name))
                {
                    events.Add("stop:" + project.Name);
                    maximum = Math.Max(maximum, ++inFlight);
                }
                return live[project.Name];
            });
        var waits = Channel.CreateUnbounded<(TimeSpan Duration, TaskCompletionSource Ready)>();
        harness.Delay = (duration, token) =>
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waits.Writer.TryWrite((duration, ready));
            return ready.Task.WaitAsync(token);
        };
        harness.Host.DeferPidReports = true;
        harness.Host.OnRun = request =>
        {
            events.Add("start:" + projects.Single(project => project.RootDirectory == request.WorkingDirectory).Name);
            inFlight--;
        };

        var restarting = harness.Coordinator().RunAsync(projects, "build-commit", default);
        var ordered = projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var wait = await Reported(waits.Reader.ReadAsync().AsTask());
            Assert.Equal(index, harness.Host.Inner.Requests.Count);
            Assert.Equal(index + 1, stopped.Count);
            Assert.Contains(ordered[index].Name, stopped);
            live[ordered[index].Name] = false;
            harness.Clock.Advance(wait.Duration);
            wait.Ready.SetResult();
            var request = await Reported(harness.Host.Requests.Reader.ReadAsync().AsTask());
            Assert.Equal(ordered[index].RootDirectory, request.WorkingDirectory);
            // A host invocation is not yet a reported start; the next project must still be untouched.
            Assert.Equal(index + 1, stopped.Count);
            Assert.Equal(index + 1, harness.Host.Inner.Requests.Count);
            var reportPid = await Reported(harness.Host.PidReports.Reader.ReadAsync().AsTask());
            reportPid();
        }

        var result = await Reported(restarting);
        Assert.Equal(new[] { "stop:A", "start:A", "stop:b", "start:b", "stop:c", "start:c" }, events);
        Assert.Equal(1, maximum);
        Assert.Equal(0, inFlight);
        Assert.Equal(new[] { "A", "b", "c" }, result.Projects.Select(project => project.Name));
        Assert.All(result.Projects, project =>
        {
            Assert.Equal("restarted", project.State);
            Assert.False(project.Child!.Completion!.IsCompleted);
        });
    }

    [Fact]
    public async Task Restart_OldPidExitedButLockOwnerLive_WaitsForLockOwnerExitBeforeStart()
    {
        using var harness = new Harness();
        var project = harness.Project("one");
        var polls = 0;
        harness.WriteLive(project, 42, () => polls < 3);
        harness.Delay = (duration, token) =>
        {
            Assert.Empty(harness.Host.Inner.Requests);
            polls++;
            return harness.Clock.Delay(duration, token);
        };

        var result = await Reported(harness.Coordinator().RunAsync([project], "build-commit", default));

        Assert.Equal(3, polls);
        Assert.Equal(0, harness.Probe.BarePidChecks);
        Assert.All(harness.Probe.Identities, identity => Assert.Equal(Harness.StartedAt, identity.StartedAt));
        Assert.Single(harness.Host.Inner.Requests);
        Assert.Equal("restarted", Assert.Single(result.Projects).State);
    }

    [Fact]
    public async Task Restart_StopTimeoutAndStartFailure_RetainOldBuildAndRestartRemaining()
    {
        using var harness = new Harness();
        var failedStart = harness.Project("a");
        var timedOut = harness.Project("b");
        var ready = harness.Project("c");
        harness.ExitAfterStop(failedStart, 1);
        harness.WriteLive(timedOut, 2, () => true);
        harness.ExitAfterStop(ready, 3);
        harness.Host.Inner.FirstFailure = "throw";
        var delays = new List<TimeSpan>();
        harness.Delay = (duration, token) =>
        {
            delays.Add(duration);
            return harness.Clock.Delay(duration, token);
        };

        var result = await Reported(harness.Coordinator(stopTimeout: TimeSpan.FromMilliseconds(250))
            .RunAsync([ready, timedOut, failedStart], "build-commit", default));

        Assert.Equal(new[] { "failed-start", "failed-stop-timeout", "restarted" },
            result.Projects.Select(project => project.State));
        Assert.Contains("launch denied", result.Projects[0].Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Projects[1].Error));
        Assert.Equal(new[] { "old-a", "old-b", "build-commit" }, result.Projects.Select(project => project.BuildCommit));
        Assert.All(result.Projects, project => Assert.Equal("old-" + project.Name, project.OldBuildCommit));
        Assert.All(result.Projects, project => Assert.Equal("build-commit", project.TargetBuildCommit));
        Assert.Null(result.Projects[0].NewBuildCommit);
        Assert.Null(result.Projects[1].NewBuildCommit);
        Assert.Equal(new[] { failedStart.RootDirectory, ready.RootDirectory },
            harness.Host.Inner.Requests.Select(request => request.WorkingDirectory));
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50) }, delays);
        Assert.False(File.Exists(timedOut.ResolveWorkspace().ConductorStopFilePath));
    }

    [Fact]
    public async Task Restart_RenderedTable_ShowsNewBuildForRestartedAndOldBuildForFailed()
    {
        using var harness = new Harness();
        var failed = harness.Project("a");
        var ready = harness.Project("b");
        harness.Host.Inner.FirstFailure = "early-exit";
        harness.ExitAfterStop(failed, 1);
        harness.ExitAfterStop(ready, 2);

        var result = await Reported(harness.Coordinator().RunAsync([ready, failed], "build-commit", default));
        using var output = new StringWriter();
        ProjectFleetStatusReport.Write(output, result.BuildRows());

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        var cells = lines.Skip(1).Select(line => line.Split('\t')).ToDictionary(row => row[0]);
        Assert.Equal("failed-start", cells["a"][1]);
        Assert.Equal("old-a", cells["a"][3]);
        Assert.Equal("restarted", cells["b"][1]);
        Assert.Equal("build-commit", cells["b"][3]);
        Assert.Equal("JAVA_HOME=fake-jdk", cells["b"][4]);
        Assert.Contains("exit 23", cells["a"][6]);
    }

    [Fact]
    public async Task Restart_HomeAndNotRunning_SkippedWithoutStopFileOrStart()
    {
        using var harness = new Harness();
        var home = harness.Project("home");
        var stopped = harness.Project("stopped");
        var disabled = harness.Project("disabled") with { Enabled = false };
        harness.WriteLive(home, 1, () => throw new InvalidOperationException("Home must not be probed."));
        harness.WriteLive(disabled, 2, () => throw new InvalidOperationException("Disabled must not be probed."));

        var result = await Reported(harness.Coordinator(home.RootDirectory + Path.DirectorySeparatorChar)
            .RunAsync([stopped, disabled, home], "build-commit", default));

        Assert.Equal(new[] { "skipped-home", "skipped-not-running" }, result.Projects.Select(project => project.State));
        Assert.Equal(new[] { "old-home", "old-stopped" }, result.Projects.Select(project => project.BuildCommit));
        Assert.Empty(harness.Host.Inner.Requests);
        Assert.All(new[] { home, stopped, disabled }, project =>
            Assert.False(File.Exists(project.ResolveWorkspace().ConductorStopFilePath)));
    }

    [Fact]
    public async Task Restart_LockOwnerExited_RemovesStopFileBeforeStart()
    {
        using var harness = new Harness();
        var project = harness.Project("one");
        var stopFile = project.ResolveWorkspace().ConductorStopFilePath;
        harness.ExitAfterStop(project, 1);
        bool? stopPresentAtStart = null;
        harness.Host.OnRun = _ => stopPresentAtStart = File.Exists(stopFile);

        var result = await Reported(harness.Coordinator().RunAsync([project], "build-commit", default));

        Assert.Equal("restarted", Assert.Single(result.Projects).State);
        Assert.Equal(false, stopPresentAtStart);
        Assert.False(File.Exists(stopFile));
    }

    [Fact]
    public async Task Restart_MalformedLock_FailedLockProbeAndNextProceeds()
    {
        using var harness = new Harness();
        var malformed = harness.Project("a");
        var ready = harness.Project("b");
        harness.Fixture.WriteLock(malformed, "invalid lock");
        harness.ExitAfterStop(ready, 2);

        var result = await Reported(harness.Coordinator().RunAsync([ready, malformed], "build-commit", default));

        Assert.Equal("failed-lock-probe", result.Projects[0].State);
        Assert.False(string.IsNullOrWhiteSpace(result.Projects[0].Error));
        Assert.Equal("old-a", result.Projects[0].BuildCommit);
        Assert.Equal("restarted", result.Projects[1].State);
        Assert.Equal(ready.RootDirectory, Assert.Single(harness.Host.Inner.Requests).WorkingDirectory);
    }

    [Fact]
    public async Task Restart_LockOwnerChanges_RechecksSuccessorIdentityBeforeStart()
    {
        using var harness = new Harness();
        var project = harness.Project("one");
        harness.WriteLive(project, 1, () => true);
        var polls = 0;
        harness.Delay = (duration, token) =>
        {
            Assert.Empty(harness.Host.Inner.Requests);
            polls++;
            if (polls == 1) harness.WriteLive(project, 2, () => polls < 3);
            return harness.Clock.Delay(duration, token);
        };

        var result = await Reported(harness.Coordinator().RunAsync([project], "build-commit", default));

        Assert.Equal(3, polls);
        Assert.Contains(harness.Probe.Identities, identity => identity.Pid == 2);
        Assert.Equal("restarted", Assert.Single(result.Projects).State);
        Assert.Single(harness.Host.Inner.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_StopTimesOut_WithdrawsOnlyItsOwnRequest(bool preexisting)
    {
        using var harness = new Harness();
        var project = harness.Project("one");
        harness.WriteLive(project, 1, () => true);
        var stopFile = project.ResolveWorkspace().ConductorStopFilePath;
        if (preexisting) File.WriteAllText(stopFile, "operator request");

        var result = await Reported(harness.Coordinator(stopTimeout: TimeSpan.Zero)
            .RunAsync([project], "build-commit", default));

        Assert.Equal("failed-stop-timeout", Assert.Single(result.Projects).State);
        Assert.Empty(harness.Host.Inner.Requests);
        Assert.Equal(preexisting, File.Exists(stopFile));
        if (preexisting) Assert.Equal("operator request", File.ReadAllText(stopFile));
    }

    [Fact]
    public async Task Restart_CancelledWhileWaiting_PropagatesAndDoesNotStopNextProject()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        var first = harness.Project("a");
        var next = harness.Project("b");
        harness.WriteLive(first, 1, () => true);
        harness.WriteLive(next, 2, () => true);
        harness.Delay = (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reported(harness.Coordinator()
            .RunAsync([next, first], "build-commit", cancellation.Token)));

        Assert.Empty(harness.Host.Inner.Requests);
        Assert.False(File.Exists(first.ResolveWorkspace().ConductorStopFilePath));
        Assert.False(File.Exists(next.ResolveWorkspace().ConductorStopFilePath));
    }

    [Fact]
    public async Task Restart_StopWriteFails_ReportsFailureAndRestartsNext()
    {
        using var harness = new Harness();
        var blocked = harness.Project("a");
        var ready = harness.Project("b");
        harness.WriteLive(blocked, 1, () => true);
        Directory.CreateDirectory(blocked.ResolveWorkspace().ConductorStopFilePath);
        harness.ExitAfterStop(ready, 2);

        var result = await Reported(harness.Coordinator().RunAsync([ready, blocked], "build-commit", default));

        Assert.Equal("failed-stop", result.Projects[0].State);
        Assert.False(string.IsNullOrWhiteSpace(result.Projects[0].Error));
        Assert.Equal("old-a", result.Projects[0].BuildCommit);
        Assert.Equal("restarted", result.Projects[1].State);
        Assert.Equal(ready.RootDirectory, Assert.Single(harness.Host.Inner.Requests).WorkingDirectory);
    }

    [Fact]
    public async Task Restart_OwnerReappearsAtStart_ReportsFailedStartWithOldCommit()
    {
        using var harness = new Harness();
        var project = harness.Project("one");
        var checks = 0;
        harness.WriteLive(project, 1, () => ++checks != 2);

        var result = await Reported(harness.Coordinator().RunAsync([project], "build-commit", default));

        var failed = Assert.Single(result.Projects);
        Assert.Equal(3, checks);
        Assert.Equal("failed-start", failed.State);
        Assert.Equal("already-running", failed.Error);
        Assert.Equal("old-one", failed.BuildCommit);
        Assert.Null(failed.NewBuildCommit);
        Assert.Empty(harness.Host.Inner.Requests);
    }

    [Fact]
    public async Task Restart_StartPathBuild_RecordsActualCommitSeparatelyFromTarget()
    {
        using var harness = new Harness();
        var project = harness.Project("one");
        harness.ExitAfterStop(project, 1);

        var result = await Reported(harness.Coordinator().RunAsync([project], "requested-commit", default));

        var restarted = Assert.Single(result.Projects);
        Assert.Equal("restarted", restarted.State);
        Assert.Equal("requested-commit", restarted.TargetBuildCommit);
        Assert.Equal("build-commit", restarted.NewBuildCommit);
        Assert.Equal("build-commit", restarted.BuildCommit);
        Assert.Single(harness.Host.Inner.Requests);
    }

    private static async Task<T> Reported<T>(Task<T> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex) { throw new TimeoutException("Rolling restart did not report the next expected event or result.", ex); }
    }

    private sealed class Harness : IDisposable
    {
        internal static readonly DateTimeOffset StartedAt = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        internal ProjectFleetSupervisorTests.Fixture Fixture { get; } = new();
        internal RecordingHost Host { get; }
        internal ScriptedProbe Probe { get; } = new();
        internal ManualClock Clock { get; } = new();
        internal Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }
        internal Harness() => Host = new(Fixture.Host);
        internal OrchestratorProject Project(string name) => Fixture.Project(name);
        internal void WriteLive(OrchestratorProject project, int pid, Func<bool> isLive)
        {
            Probe.Owners[pid] = isLive;
            Fixture.WriteLock(project, FormattableString.Invariant($"{pid}\n{StartedAt:O}\n{StartedAt:O}\n"));
        }
        internal void ExitAfterStop(OrchestratorProject project, int pid)
        {
            var exited = false;
            WriteLive(project, pid, () =>
            {
                if (File.Exists(project.ResolveWorkspace().ConductorStopFilePath)) exited = true;
                return !exited;
            });
        }
        internal ProjectFleetRollingRestart Coordinator(string? homeRoot = null, TimeSpan? stopTimeout = null) =>
            new(new ProjectFleetSupervisor(Host,
                    new ProjectEnvironmentResolver(() => new Dictionary<string, string> { ["JAVA_HOME"] = "fake-jdk" },
                        () => new Dictionary<string, string>()), Probe, () => "build-commit", homeRoot),
                project => "old-" + project.Name, Clock, Probe, homeRoot, stopTimeout,
                delay: (duration, token) => Delay is null ? Clock.Delay(duration, token) : Delay(duration, token));
        public void Dispose() => Fixture.Dispose();
    }

    private sealed class RecordingHost(ProjectFleetSupervisorTests.FakeHost inner) : IConductorSupervisorProcessHost
    {
        internal ProjectFleetSupervisorTests.FakeHost Inner => inner;
        internal Action<ConductorSupervisorProcessRequest>? OnRun { get; set; }
        internal bool DeferPidReports { get; set; }
        internal Channel<Action> PidReports { get; } = Channel.CreateUnbounded<Action>();
        internal Channel<ConductorSupervisorProcessRequest> Requests { get; } = Channel.CreateUnbounded<ConductorSupervisorProcessRequest>();
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken token)
        {
            OnRun?.Invoke(request);
            var completion = inner.RunAsync(DeferPidReports ? request with
            {
                OnProcessStarted = pid => PidReports.Writer.TryWrite(() => request.OnProcessStarted!(pid))
            } : request, token);
            Requests.Writer.TryWrite(request);
            return completion;
        }
    }

    private sealed class ScriptedProbe : IConductLockPidProbe
    {
        internal Dictionary<int, Func<bool>> Owners { get; } = [];
        internal List<(int Pid, DateTimeOffset StartedAt)> Identities { get; } = [];
        internal int BarePidChecks { get; private set; }
        public bool IsRunning(int processId) { BarePidChecks++; return false; }
        public bool IsSameProcess(int processId, DateTimeOffset processStartedAt)
        {
            Identities.Add((processId, processStartedAt));
            return Owners[processId]();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => Harness.StartedAt + TimeSpan.FromTicks(ticks);
        internal void Advance(TimeSpan duration) => ticks += duration.Ticks;
        internal Task Delay(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Advance(duration);
            return Task.CompletedTask;
        }
    }
}
