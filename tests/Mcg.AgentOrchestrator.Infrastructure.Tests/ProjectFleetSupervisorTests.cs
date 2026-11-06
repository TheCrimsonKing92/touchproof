using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using static InfrastructureTestSupport;

// Parallel-safe: private roots and injected process/lock/environment probes; no real children.
public sealed class ProjectFleetSupervisorTests
{
    [Fact]
    public async Task Start_EnabledLiveAndBlockedProjects_StartsOnlyEligibleWithEnvironment()
    {
        using var fixture = new Fixture();
        var ready = fixture.Project("A");
        var disabled = fixture.Project("B") with { Enabled = false };
        var live = fixture.Project("C");
        fixture.WriteLock(live);
        var blocked = fixture.Project("D") with { ToolRequirements = [new("Gradle", "GRADLE_HOME")] };

        var pass = await Reported(fixture.Supervisor().StartAsync([ready, disabled, live, blocked], default));

        var request = Assert.Single(fixture.Host.Requests);
        Assert.Equal(new[] { "conduct", "--loop", "--project=A" }, request.Arguments);
        Assert.Equal(ready.RootDirectory, request.WorkingDirectory);
        Assert.Equal("user-jdk", request.AdditionalEnvironment!["JAVA_HOME"]);
        Assert.Equal("machine-bin" + Path.PathSeparator + "user-bin", request.AdditionalEnvironment["Path"]);
        Assert.Equal(2, request.AdditionalEnvironment.Count);
        Assert.False(request.AdditionalEnvironment.ContainsKey(OrchestratorWorkspace.RepoRootEnvironmentVariable));
        Assert.False(request.AdditionalEnvironment.ContainsKey(ConductorContinuitySupervisor.StdoutLogPathEnvironmentVariable));
        Assert.False(request.AdditionalEnvironment.ContainsKey(ConductorContinuitySupervisor.StderrLogPathEnvironmentVariable));
        Assert.Equal(new[] { "A", "C", "D" }, pass.Children.Select(child => child.Name));
        Assert.Equal("started", pass.Children[0].State);
        Assert.Equal(1001, pass.Children[0].ProcessId);
        Assert.Equal("already-running", pass.Children[1].State);
        Assert.Equal("missing-requirements", pass.Children[2].State);
        Assert.Equal(new[] { "Gradle (GRADLE_HOME)" }, pass.Children[2].Resolution.Missing);
        Assert.False(pass.Children[0].Completion!.IsCompleted);
    }

    [Fact]
    public async Task Start_TwoEligibleProjects_ReportsBothBeforeEitherChildExits()
    {
        using var fixture = new Fixture();
        var pass = await Reported(fixture.Supervisor().StartAsync(
            [fixture.Project("one"), fixture.Project("two")], default));
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.All(pass.Children, child =>
        {
            Assert.Equal("started", child.State);
            Assert.False(child.Completion!.IsCompleted);
        });
        fixture.Host.Complete();
        Assert.All(await Task.WhenAll(pass.Children.Select(child => child.Completion!)),
            result => Assert.Equal(0, result.ExitCode));
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("early-exit")]
    public async Task Start_OneHostFailure_ReportsFailureAndStillStartsNextProject(string mode)
    {
        using var fixture = new Fixture();
        fixture.Host.FirstFailure = mode;
        var pass = await Reported(fixture.Supervisor().StartAsync(
            [fixture.Project("A"), fixture.Project("B")], default));
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.Equal("start-failed", pass.Children[0].State);
        Assert.Contains(mode == "throw" ? "launch denied" : "exit 23", pass.Children[0].Error);
        Assert.Equal("started", pass.Children[1].State);
    }

    [Fact]
    public async Task Start_DeferredPidCallbacks_StartsAllThenWaitsForPidReports()
    {
        using var fixture = new Fixture();
        fixture.Host.DelayStarted = true;
        var starting = fixture.Supervisor().StartAsync([fixture.Project("A"), fixture.Project("B")], default);
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.False(starting.IsCompleted);
        fixture.Host.ReportStarts();
        var pass = await Reported(starting);
        Assert.Equal(new int?[] { 1001, 1002 }, pass.Children.Select(child => child.ProcessId));
        Assert.All(pass.Children, child => Assert.False(child.Completion!.IsCompleted));
    }

    [Fact]
    public async Task Start_HomeAndMalformedLock_SkipsBothWithoutCallingHost()
    {
        using var fixture = new Fixture();
        var home = fixture.Project("home");
        var invalid = fixture.Project("invalid");
        fixture.WriteLock(invalid, "malformed");
        var pass = await fixture.Supervisor(home.RootDirectory).StartAsync([home, invalid], default);
        Assert.Empty(fixture.Host.Requests);
        Assert.Equal(new[] { "home-skipped", "lock-invalid" }, pass.Children.Select(child => child.State));
    }

    [Fact]
    public async Task Start_RecycledLockOwner_StartsProjectUsingIdentityProbe()
    {
        using var fixture = new Fixture();
        var project = fixture.Project("recycled");
        fixture.WriteLock(project);
        fixture.Probe.SameProcess = false;
        var pass = await Reported(fixture.Supervisor().StartAsync([project], default));
        Assert.Single(fixture.Host.Requests);
        Assert.Equal("started", Assert.Single(pass.Children).State);
        Assert.Equal(1, fixture.Probe.IdentityChecks);
    }

    [Fact]
    public async Task Start_UnreadableEnvironment_BlocksEvenWithoutRequirements()
    {
        using var fixture = new Fixture();
        var resolver = new ProjectEnvironmentResolver(() => throw new IOException("scope unreadable"),
            () => new Dictionary<string, string>());
        var supervisor = new ProjectFleetSupervisor(fixture.Host, resolver, fixture.Probe, () => "build");
        var pass = await supervisor.StartAsync([fixture.Project("A") with { ToolRequirements = [] }], default);
        Assert.Empty(fixture.Host.Requests);
        Assert.Equal("environment-unavailable", Assert.Single(pass.Children).State);
        Assert.Equal("scope unreadable", pass.Children[0].Error);
    }

    [Fact]
    public async Task Command_Run_PrintsStartsThenWaitsForChildCompletion()
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        var command = ProjectFleetCommand.RunAsync([fixture.Project("A"), fixture.Project("B")],
            fixture.Supervisor(), output, default);
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.Contains("A\tstarted\t1001\tbuild-commit", output.ToString());
        Assert.Contains("B\tstarted\t1002\tbuild-commit", output.ToString());
        Assert.False(command.IsCompleted);
        fixture.Host.Complete();
        Assert.Equal(0, await command);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Command_NoLaunchableProjects_ReportsEmptyOrBlocked(bool blocked, int expected)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        var projects = blocked ? new[] { fixture.Project("blocked") with
            { ToolRequirements = [new("JDK", "ABSENT")] } } : Array.Empty<OrchestratorProject>();
        Assert.Equal(expected, await ProjectFleetCommand.RunAsync(projects, fixture.Supervisor(), output, default));
        Assert.Empty(fixture.Host.Requests);
        Assert.Contains(blocked ? "JDK (ABSENT)" : "No enabled projects.", output.ToString());
    }

    [Fact]
    public async Task Command_Cancelled_WaitsForEveryStartedChildToFinish()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        var command = ProjectFleetCommand.RunAsync([fixture.Project("A"), fixture.Project("B")],
            fixture.Supervisor(), output, cancellation.Token);
        Assert.Equal(2, fixture.Host.Requests.Count);
        cancellation.Cancel();
        Assert.False(command.IsCompleted);
        fixture.Host.Complete();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
    }

    internal static async Task<ProjectFleetStartPass> Reported(Task<ProjectFleetStartPass> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException ex) { throw new TimeoutException("Fleet did not report child startup outcomes.", ex); }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string directory = CreateTempDirectory();
        internal FakeHost Host { get; } = new();
        internal FakeProbe Probe { get; } = new();
        internal OrchestratorProject Project(string name) => new(name,
            Directory.CreateDirectory(Path.Combine(directory, name)).FullName)
        {
            Enabled = true, DataRootDirectory = Path.Combine(directory, "data"),
            ToolRequirements = [new("JDK", "JAVA_HOME")]
        };
        internal ProjectFleetSupervisor Supervisor(string? homeRoot = null) => new(Host,
            new ProjectEnvironmentResolver(() => new Dictionary<string, string>
            {
                ["JAVA_HOME"] = "machine-jdk", ["Path"] = "machine-bin",
                [OrchestratorWorkspace.RepoRootEnvironmentVariable] = "wrong-root",
                [ConductorContinuitySupervisor.StdoutLogPathEnvironmentVariable] = "wrong-out",
                [ConductorContinuitySupervisor.StderrLogPathEnvironmentVariable] = "wrong-err"
            }, () => new Dictionary<string, string> { ["JAVA_HOME"] = "user-jdk", ["Path"] = "user-bin" }),
            Probe, () => "build-commit", homeRoot);
        internal void WriteLock(OrchestratorProject project, string? contents = null)
        {
            var path = project.ResolveWorkspace().OrchestratorDirectory;
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "conduct-loop.lock"), contents ??
                "42\n2026-11-01T00:00:00.0000000+00:00\n2026-11-01T00:00:00.0000000+00:00\n");
        }
        public void Dispose() { Host.Complete(); Directory.Delete(directory, recursive: true); }
    }

    internal sealed class FakeProbe : IConductLockPidProbe
    {
        internal bool SameProcess { get; set; } = true;
        internal int IdentityChecks { get; private set; }
        public bool IsRunning(int processId) => processId == 42;
        public bool IsSameProcess(int processId, DateTimeOffset startedAt)
        { IdentityChecks++; return processId == 42 && SameProcess; }
    }

    internal sealed class FakeHost : IConductorSupervisorProcessHost
    {
        internal List<ConductorSupervisorProcessRequest> Requests { get; } = [];
        private readonly List<(int Pid, TaskCompletionSource<ConductorSupervisorProcessResult> Source)> pending = [];
        private readonly List<Action> startCallbacks = [];
        internal string? FirstFailure { get; set; }
        internal bool DelayStarted { get; set; }
        public Task<ConductorSupervisorProcessResult> RunAsync(
            ConductorSupervisorProcessRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (Requests.Count == 1 && FirstFailure == "throw") throw new IOException("launch denied");
            if (Requests.Count == 1 && FirstFailure == "early-exit")
                return Task.FromResult(new ConductorSupervisorProcessResult(23, 999));
            var pid = 1000 + Requests.Count;
            var source = new TaskCompletionSource<ConductorSupervisorProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add((pid, source));
            if (DelayStarted) startCallbacks.Add(() => request.OnProcessStarted!(pid));
            else request.OnProcessStarted!(pid);
            return source.Task;
        }
        internal void ReportStarts() { foreach (var callback in startCallbacks) callback(); }
        internal void Complete()
        {
            foreach (var (pid, source) in pending) source.TrySetResult(new(0, pid));
        }
    }
}
