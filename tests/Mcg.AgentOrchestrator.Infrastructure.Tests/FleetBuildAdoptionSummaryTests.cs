using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns its registry, data root, logs, clock and PID probe.
public sealed class FleetBuildAdoptionSummaryTests
{
    private static readonly string C = new('c', 40);
    private static readonly string D = new('d', 40);

    [Xunit.Fact]
    public void AllRunningProjectsAdopted_WritesExactlyOneSummary()
    {
        using var fixture = new Fixture();
        fixture.AddProject("c");
        fixture.AddProject("a");
        fixture.AddProject("b");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        Assert.Empty(fixture.Lines());
        fixture.Clock.Advance(TimeSpan.FromSeconds(12));
        foreach (var name in new[] { "a", "b", "c" }) fixture.Event(name, $"ACTIVATION_ADOPTED adoptedCommit={C}");
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted=a,b,c pending= failed= elapsedSeconds=12",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void SilentProjectAtDeadline_IsPending()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        fixture.AddProject("b");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={C}");
        fixture.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        summary.Tick();
        Assert.Empty(fixture.Lines());
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted=a pending=b failed= elapsedSeconds=900",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void RevertedProject_IsFailedAtDeadline()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Event("a", $"ACTIVATION_REVERTED failedCommit={C} restoredCommit={D}");
        summary.Tick();
        Assert.Empty(fixture.Lines()); // Failure does not satisfy the all-adopted trigger.
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted= pending= failed=a elapsedSeconds=900",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void NotRunningProject_IsInNoField()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        fixture.AddProject("stopped", running: false);
        fixture.AddProject("recycled", sameProcess: false);
        fixture.AddProject("malformed");
        File.WriteAllText(fixture.LockPath("malformed"), "not-a-pid");
        fixture.AddProject("home", root: fixture.HomeRoot);
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={C}");
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted=a pending= failed= elapsedSeconds=0",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void NewerAdoptionWhilePending_FinalizesPreviousThenWaits()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        fixture.AddProject("b");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={C}");
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        summary.OnHomeAdopted(D);
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted=a pending=b failed= elapsedSeconds=10",
            Assert.Single(fixture.Lines()));
        summary.Tick();
        Assert.Single(fixture.Lines());
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={D}");
        fixture.Event("b", $"ACTIVATION_ADOPTED adoptedCommit={D}");
        summary.Tick();
        Assert.Equal(new[]
        {
            $"FLEET_BUILD_ADOPTED commit={C} adopted=a pending=b failed= elapsedSeconds=10",
            $"FLEET_BUILD_ADOPTED commit={D} adopted=a,b pending= failed= elapsedSeconds=0"
        }, fixture.Lines());
    }

    [Xunit.Fact]
    public void RepeatedTicksAndAdoptions_NeverWriteSecondSummary()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        summary.OnHomeAdopted(C.ToUpperInvariant()); // Does not restart the deadline/window.
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={C.ToUpperInvariant()}");
        summary.Tick();
        summary.Tick();
        summary.OnHomeAdopted(C.ToUpperInvariant());
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted=a pending= failed= elapsedSeconds=4",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void HandoffFailedThenAdoptedOnRetry_IsAdopted()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Event("a", $"LOOP_HANDOFF_FAILED commit={C}");
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={C}");
        // Append older failure last: classification must follow timestamps, not append order.
        fixture.Event("a", $"ACTIVATION_REVERTED failedCommit={C}", fixture.Clock.Now.AddSeconds(-1));
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted=a pending= failed= elapsedSeconds=2",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void AdoptedThenReverted_IsFailed()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={C}");
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        fixture.Event("a", $"ACTIVATION_REVERTED failedCommit={C}");
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted= pending= failed=a elapsedSeconds=902",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Theory]
    [Xunit.InlineData("commit")]
    [Xunit.InlineData("failedCommit")]
    public void CommitSpecificHandoffFailure_IsFailed(string field)
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Event("a", $"LOOP_HANDOFF_FAILED {field}={C}");
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted= pending= failed=a elapsedSeconds=900",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Theory]
    [Xunit.InlineData("ACTIVATION_ADOPTED")]
    [Xunit.InlineData("ACTIVATION_REVERTED")]
    public void EqualTimestamps_LastMatchingLineWins(string lastEvent)
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={C}");
        fixture.Event("a", $"ACTIVATION_REVERTED failedCommit={C}");
        fixture.Event("a", $"{lastEvent} adoptedCommit={C} failedCommit={C}");
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        summary.Tick();
        var fields = lastEvent == "ACTIVATION_ADOPTED" ? "adopted=a pending= failed=" : "adopted= pending= failed=a";
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} {fields} elapsedSeconds=900", Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void StaleUnrelatedAndMalformedRecords_DoNotResolveProject()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={C}", fixture.Clock.Now.AddSeconds(-1));
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={D}");
        fixture.Event("a", $"ACTIVATION_REVERTED failedCommit={D} restoredCommit={C}");
        fixture.Event("a", $"LOOP_HANDOFF_FAILED reason=unknown restoredCommit={C}");
        fixture.Event("a", $"OTHER detail=ACTIVATION_ADOPTED adoptedCommit={C}");
        File.AppendAllText(fixture.LogPath("a"), "{\n[]\n{\"timestamp\":false,\"detail\":3}\n");
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted= pending=a failed= elapsedSeconds=900",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void RotatedLog_StillSuppliesAdoptionEvidence()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={C}");
        File.Move(fixture.LogPath("a"), Path.Combine(Path.GetDirectoryName(fixture.LogPath("a"))!,
            "conduct-events-20261102090000.log"));
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted=a pending= failed= elapsedSeconds=0",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void RunningFleetChanges_DoNotChangeAdoptionSnapshot()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        fixture.AddProject("stopped", running: false);
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        File.Delete(fixture.LockPath("a"));
        fixture.Start("stopped");
        fixture.AddProject("later");
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted= pending=a failed= elapsedSeconds=900",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void MissingOrLockedLog_RetriesWithoutInventingFailure()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        summary.Tick();
        Assert.Empty(fixture.Lines());
        fixture.Event("a", $"ACTIVATION_ADOPTED adoptedCommit={C}");
        using (var held = new FileStream(fixture.LogPath("a"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            summary.Tick();
            Assert.Empty(fixture.Lines());
        }
        summary.Tick();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted=a pending= failed= elapsedSeconds=0",
            Assert.Single(fixture.Lines()));
    }

    [Xunit.Fact]
    public void TimerCallback_SummarizesSilentFleetAndDisposesTimer()
    {
        using var fixture = new Fixture();
        fixture.AddProject("a");
        using var summary = fixture.CreateSummary(pollInterval: TimeSpan.FromSeconds(5));
        summary.OnHomeAdopted(C);
        var timer = Assert.IsType<ManualTimer>(fixture.Clock.Timer);
        Assert.Equal(TimeSpan.FromSeconds(5), timer.DueTime);
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        timer.Fire();
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted= pending=a failed= elapsedSeconds=900",
            Assert.Single(fixture.Lines()));
        Assert.True(timer.Disposed);
        timer.Fire(); // A queued callback after disposal is harmless.
        Assert.Single(fixture.Lines());
    }

    [Xunit.Fact]
    public void EmptyFleet_SummarizesImmediatelyWithoutTimer()
    {
        using var fixture = new Fixture();
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted(C);
        Assert.Equal($"FLEET_BUILD_ADOPTED commit={C} adopted= pending= failed= elapsedSeconds=0",
            Assert.Single(fixture.Lines()));
        Assert.Null(fixture.Clock.Timer);
    }

    [Xunit.Fact]
    public void InvalidCommit_DoesNotStartObservation()
    {
        using var fixture = new Fixture();
        using var summary = fixture.CreateSummary();
        summary.OnHomeAdopted("incumbent");
        summary.Tick();
        Assert.Empty(fixture.Lines());
        Assert.Null(fixture.Clock.Timer);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "fleet-summary-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, OrchestratorProject> _projects = new();
        private readonly PidProbe _probe = new();
        private readonly OrchestratorProjectRegistry _registry;
        private readonly ConductEventLogWriter _homeLog;
        internal ManualClock Clock { get; } = new();
        internal string HomeRoot => Path.Combine(_root, "home");
        private string HomeLogPath => Path.Combine(HomeRoot, "conduct-events.log");

        internal Fixture()
        {
            Directory.CreateDirectory(HomeRoot);
            _registry = new OrchestratorProjectRegistry(Path.Combine(_root, "registry"), Path.Combine(_root, "data"));
            _homeLog = new ConductEventLogWriter(HomeLogPath);
        }

        internal void AddProject(string name, bool running = true, bool sameProcess = true, string? root = null)
        {
            root ??= Path.Combine(_root, "sources", name);
            Directory.CreateDirectory(root);
            _projects.Add(name, _registry.CreateProject(name, root));
            if (running) Start(name, sameProcess);
        }

        internal void Start(string name, bool sameProcess = true)
        {
            var pid = _probe.Identities.Count + 1;
            var started = Clock.Now.AddMinutes(-1);
            _probe.Identities.Add(pid, sameProcess ? started : started.AddSeconds(-1));
            Directory.CreateDirectory(Path.GetDirectoryName(LockPath(name))!);
            File.WriteAllLines(LockPath(name), [pid.ToString(CultureInfo.InvariantCulture),
                Clock.Now.ToString("O", CultureInfo.InvariantCulture), started.ToString("O", CultureInfo.InvariantCulture)]);
        }

        internal string LockPath(string name) => Path.Combine(_projects[name].ResolveWorkspace().OrchestratorDirectory, "conduct-loop.lock");
        internal string LogPath(string name) => _projects[name].ResolveWorkspace().ConductEventsLogPath;
        internal void Event(string name, string detail, DateTimeOffset? at = null) =>
            new ConductEventLogWriter(LogPath(name)).Append("loop-handoff", null, detail, at ?? Clock.Now);

        internal FleetBuildAdoptionSummary CreateSummary(TimeSpan? pollInterval = null) => new(
            _registry, line => _homeLog.Append("loop-handoff", null, line, Clock.Now), Clock,
            pidProbe: _probe, homeRoot: HomeRoot, pollInterval: pollInterval ?? Timeout.InfiniteTimeSpan);

        internal string[] Lines() => !File.Exists(HomeLogPath) ? [] : File.ReadAllLines(HomeLogPath).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.GetProperty("detail").GetString()!;
        }).ToArray();

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class PidProbe : IConductLockPidProbe
    {
        internal Dictionary<int, DateTimeOffset> Identities { get; } = new();
        public bool IsRunning(int processId) => Identities.ContainsKey(processId);
        public bool IsSameProcess(int processId, DateTimeOffset processStartedAt) =>
            Identities.TryGetValue(processId, out var started) && started == processStartedAt;
    }

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now { get; private set; } = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        internal ManualTimer? Timer { get; private set; }
        public override DateTimeOffset GetUtcNow() => Now;
        internal void Advance(TimeSpan amount) => Now += amount;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new ManualTimer(callback, state, dueTime);
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        internal TimeSpan DueTime { get; private set; } = dueTime;
        internal bool Disposed { get; private set; }
        internal void Fire() => callback(state);
        public bool Change(TimeSpan due, TimeSpan period) { DueTime = due; return !Disposed; }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
