using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated registry/state roots, injected process host and restart runner; no global overrides.
public sealed class FleetBuildRollingRestartTriggerTests
{
    private const string First = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task CommitsAreDeduplicatedAndQueuedInOrder()
    {
        var directory = CreateTempDirectory();
        try
        {
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new ConcurrentQueue<string>();
            var lines = new ConcurrentQueue<string>();
            var path = Path.Combine(directory, "status.txt");
            var trigger = new FleetBuildRollingRestartTrigger(() => [], async (_, commit, _, _) =>
            {
                calls.Enqueue(commit);
                if (commit == First)
                {
                    firstStarted.SetResult();
                    await release.Task;
                }
                else Assert.True(release.Task.IsCompleted, "The second commit ran before the first pass was released.");
                return new([new(commit, "restarted", "old", commit, commit)]);
            }, lines.Enqueue, path, schedule: body => body());
            trigger.OnHomeAdopted(First);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang detector: first pass not scheduled.
            trigger.OnHomeAdopted(First.ToUpperInvariant());
            trigger.OnHomeAdopted(Second);
            trigger.OnHomeAdopted(Second);
            Assert.Equal([First], calls.ToArray());
            release.SetResult();
            await trigger.WhenIdle().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal([First, Second], calls.ToArray());
            Assert.Equal(2, lines.Count(line => line.StartsWith("FLEET_ROLLING_RESTART commit=")));
            Assert.Equal(2, lines.Count(line => line.StartsWith("FLEET_ROLLING_RESTART_PROJECT ")));
            Assert.Contains(Second, File.ReadAllText(path));
            Assert.DoesNotContain(First, File.ReadAllText(path)); // The current table replaces the first pass.
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FailedProjectKeepsOldBuildAndLaterProjectsStillRestart()
    {
        using var fixture = new ProjectFleetRollingRestartDrainTests.Harness();
        var projects = new[] { fixture.Project("a"), fixture.Project("b"), fixture.Project("c") };
        fixture.Host.FailingProject = "b";
        var lines = new ConcurrentQueue<string>();
        var status = Path.Combine(projects[0].RootDirectory, "status.txt");
        ProjectFleetRollingRestartResult? result = null;
        var restart = fixture.Restart(p => { fixture.Acknowledge(p, 0); return false; });
        var trigger = new FleetBuildRollingRestartTrigger(() => projects, async (snapshot, commit, _, token) =>
            result = await restart.RunAsync(snapshot, commit, token), lines.Enqueue, status);
        trigger.OnHomeAdopted(First);
        await trigger.WhenIdle().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["--project=a", "--project=b", "--project=c"],
            fixture.Host.Requests.Select(request => request.Arguments.Last()));
        Assert.Equal(["restarted", "failed-start", "restarted"], result!.Projects.Select(p => p.State));
        Assert.Equal([First, "old-b", First], result.BuildRows().Select(row => row.BuildCommit));
        Assert.Equal("launch denied", result.BuildRows()[1].Error);
        var table = File.ReadAllText(status);
        Assert.Contains($"a\trestarted\t101\t{First}", table);
        Assert.Contains("b\tfailed-start\t-\told-b", table);
        Assert.Contains("launch denied", table);
        Assert.Contains($"c\trestarted\t103\t{First}", table);
        Assert.Equal(3, lines.Count(line => line.StartsWith("FLEET_ROLLING_RESTART_PROJECT ")));
        Assert.Contains(lines, line => line.Contains("restarted=2 failed=1 skipped=0 total=3"));
    }

    [Fact]
    public async Task FailedPassIsObservedAndReservedWhileNextCommitStillRuns()
    {
        var directory = CreateTempDirectory();
        try
        {
            var calls = new ConcurrentQueue<string>();
            var lines = new ConcurrentQueue<string>();
            var trigger = new FleetBuildRollingRestartTrigger(() => [], (_, commit, _, _) =>
            {
                calls.Enqueue(commit);
                return commit == First ? throw new IOException("pass failed")
                    : Task.FromResult(new ProjectFleetRollingRestartResult([]));
            }, lines.Enqueue, Path.Combine(directory, "status.txt"));
            trigger.OnHomeAdopted(First);
            await trigger.WhenIdle().WaitAsync(TimeSpan.FromSeconds(30));
            trigger.OnHomeAdopted(First);
            trigger.OnHomeAdopted(Second);
            await trigger.WhenIdle().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal([First, Second], calls.ToArray());
            Assert.Contains($"FLEET_ROLLING_RESTART_FAILED commit={First} reason=pass failed", lines);
            Assert.Equal(2, lines.Count(line => line.StartsWith("FLEET_ROLLING_RESTART commit=")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AdoptedBuildHostUsesTheAdoptedLaunchPrefix()
    {
        var inner = new ProjectFleetRollingRestartDrainTests.Host();
        var prefix = new[] { "dotnet", "adopted/Mcg.AgentOrchestrator.App.dll" };
        var host = new FleetBuildRollingRestartTrigger.AdoptedBuildHost(inner, prefix);
        host.RunAsync(new(["conduct"], "project", "exit", "stdout", "stderr", OnProcessStarted: _ => { }), default)
            .GetAwaiter().GetResult();
        Assert.Equal(prefix, Assert.Single(inner.Requests).CommandPrefix);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("pass")]
    [InlineData("schedule")]
    [InlineData("summary")]
    public async Task AdoptionCompletesDespiteTriggerAndPassFailures(string mode)
    {
        var directory = CreateTempDirectory();
        try
        {
            var lines = new ConcurrentQueue<string>();
            var events = new RecordingEvents();
            var registry = new OrchestratorProjectRegistry(Path.Combine(directory, "registry"), Path.Combine(directory, "data"));
            using var summary = new FleetBuildAdoptionSummary(registry, lines.Enqueue, TimeProvider.System,
                pollInterval: Timeout.InfiniteTimeSpan);
            var trigger = new FleetBuildRollingRestartTrigger(() => [], (_, _, _, _) =>
                mode == "pass" ? throw new IOException("pass boom")
                    : Task.FromResult(new ProjectFleetRollingRestartResult([])), lines.Enqueue,
                Path.Combine(directory, "status.txt"),
                schedule: mode == "schedule" ? _ => throw new InvalidOperationException("schedule boom") : null);
            ConductorPreparedSuccessor Build(string commit, string name) => new(Path.Combine(directory, name),
                Path.Combine(directory, name, "Mcg.AgentOrchestrator.App.dll"), commit, commit,
                "LOOP_START selfCheck=true", new MemoryStream());
            var stages = new Queue<ConductorPreparedSuccessor>([Build(First, "first"), Build(Second, "second")]);
            var exit = new TaskCompletionSource<ConductorSupervisorProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            ConductorSupervisorProcessRequest? last = null;
            var adopted = 0;
            events.OnAppend = evt =>
            {
                if (evt.Operation != "activation" || evt.Status != "adopted" || ++adopted != 2) return;
                ConductorContinuityExitArtifact.Write(last!.ExitArtifactPath, new("stop-file", 3, 0, false));
                exit.SetResult(new(0, 102));
            };
            var host = new ScriptedHost((request, index, _) =>
            {
                if (index > 0)
                {
                    request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                    request.OnStandardOutputLine("LOOP_START tick=0");
                    for (var tick = 1; tick <= 3; tick++) request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
                }
                if (index == 2) { last = request; return exit.Task; }
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("max-duration", 3, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 100 + index));
            });
            var supervisor = new ConductorContinuitySupervisor(host, events,
                inspectStores: () => [new("fixture", "fixture.db", 1, 1)], stageSuccessor: _ => stages.Dequeue(),
                activationHealthyTicks: 3, activationDelay: (_, _) => throw new InvalidOperationException("Unexpected activation deadline"),
                appendConductEvent: (_, _, line) => lines.Enqueue(line),
                verifyRunClosure: (folder, _, _) => new(true, folder, 0, 0, "none", "fixture"),
                homeBuildPublisher: new HomeBuildAdoptionPublisher(directory, Path.Combine(directory, "home.json")))
            {
                FleetAdoptionSummaryFactory = () => mode == "summary" ? throw new InvalidOperationException("summary boom") : summary,
                FleetRollingRestartTriggerFactory = () => mode == "factory" ? throw new InvalidOperationException("factory boom") : trigger
            };
            var result = await supervisor.RunAsync(["conduct", "--loop"], directory, Path.Combine(directory, "runs"),
                "default", "default", cancellationToken: TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30));
            await trigger.WhenIdle().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, result);
            Assert.Equal(3, host.Requests.Count);
            Assert.Contains(events.Events, evt => evt.Operation == "activation" && evt.Status == "adopted" &&
                evt.Detail.Contains("adoptedCommit=" + Second));
            if (mode == "summary")
                Assert.Contains(lines, line => line.StartsWith($"FLEET_ROLLING_RESTART commit={Second} "));
            else
                Assert.Contains($"FLEET_ROLLING_RESTART_FAILED commit={Second} reason={mode} boom", lines);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ScriptedHost(Func<ConductorSupervisorProcessRequest, int, CancellationToken,
        Task<ConductorSupervisorProcessResult>> step) : IConductorSupervisorProcessHost
    {
        internal List<ConductorSupervisorProcessRequest> Requests { get; } = [];
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken token)
        {
            var index = Requests.Count;
            Requests.Add(request);
            return step(request, index, token);
        }
    }

    private sealed class RecordingEvents : IRunEventStore
    {
        internal List<RunEventAppend> Events { get; } = [];
        internal Action<RunEventAppend>? OnAppend { get; set; }
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default)
        {
            Events.Add(evt);
            OnAppend?.Invoke(evt);
            return Task.FromResult(new RunEventRecord(Events.Count, evt.EventId ?? Guid.NewGuid().ToString("N"),
                evt.OccurredAt ?? DateTimeOffset.MinValue, evt.EventType, evt.GoalId, evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
        }
        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0, string? goalId = null,
            int maxCount = 500, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
