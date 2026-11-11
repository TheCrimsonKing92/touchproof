using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace/kernel, fixed clock and AsyncLocal output capture.
public sealed class ConductorProviderHoldWatchLoopTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void Run_StandaloneProbeDefersWhileGateIsLive()
    {
        using var workspace = new ConductorVerbStartTests.Fixture();
        var calls = 0;
        var gates = 1;
        var fixture = new WatchFixture(workspace.Workspace, exhausted: true,
            probe: (kernel, _) =>
            {
                calls++;
                var source = kernel.Goals.Single(goal => goal.Status == GoalStatus.Failed);
                kernel.RecordTaskNote(source.Id, source.Tasks.Single().Id, "Fixture standalone probe checkpoint");
                return [source.Id];
            }, gates: () => gates);
        fixture.Run(1);
        Assert.Equal(0, calls);
        gates = 0;
        fixture.Run(1);
        Assert.Equal(1, calls);
        Assert.Contains(fixture.Source.Id, fixture.Persisted);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(System.Text.Json.JsonException))]
    [InlineData(typeof(InvalidDataException))]
    public void Run_GateCountThrows_DefersProbeAndCompletesTick(Type exceptionType)
    {
        using var workspace = new ConductorVerbStartTests.Fixture();
        var calls = 0;
        var gateReads = 0;
        var failure = (Exception)Activator.CreateInstance(exceptionType, "fixture gate read failure")!;
        var fixture = new WatchFixture(workspace.Workspace, exhausted: true,
            probe: (_, _) =>
            {
                calls++;
                return [];
            }, gates: () =>
            {
                gateReads++;
                throw failure;
            });

        var text = fixture.Run(1);

        Assert.Equal(1, gateReads);
        Assert.Equal(0, calls);
        Assert.Contains("LOOP_JANITORIAL_FAILED tick=1 phase=count-provider-hold-probe-gates", text);
        Assert.Contains($"exception={exceptionType.Name}", text);
        Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
    }

    [Fact]
    public void Run_RepeatedHeldTicksAndSuccessfulVerification_AnnounceOnceEach()
    {
        using var workspace = new ConductorVerbStartTests.Fixture();
        var fixture = new WatchFixture(workspace.Workspace, exhausted: true);
        fixture.Run(2);
        var placed = Assert.Single(fixture.Events());
        Assert.Equal("provider-hold-placed", placed.EventKind);
        Assert.Equal(fixture.Source.Id.Value, placed.GoalId);
        fixture.Succeed(verifiedSuccess: true);
        fixture.Run(1);
        var events = fixture.Events();
        Assert.Equal(new[] { "provider-hold-placed", "provider-hold-restored" }, events.Select(evt => evt.EventKind));
    }

    [Fact]
    public void Run_UnverifiedSuccess_RetainsHoldWithoutRestoration()
    {
        using var workspace = new ConductorVerbStartTests.Fixture();
        var fixture = new WatchFixture(workspace.Workspace, exhausted: true);
        fixture.Run(2);
        var placed = Assert.Single(fixture.Events());
        Assert.Equal("provider-hold-placed", placed.EventKind);
        fixture.Succeed();
        fixture.Run(1);
        Assert.Equal("provider-hold-placed", Assert.Single(fixture.Events()).EventKind);
        Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
    }

    [Fact]
    public void Run_WithoutExhaustion_MatchesInertWatchBytes()
    {
        using var workspace = new ConductorVerbStartTests.Fixture();
        var fixture = new WatchFixture(workspace.Workspace, exhausted: false);
        ConductorProviderHoldWatch.Attach(fixture.Loop, new ConductorProviderHoldWatch(null));
        var before = fixture.Bytes();
        fixture.Run(2);
        var baseline = fixture.Bytes()[before.Length..];
        Assert.NotEmpty(baseline);
        fixture.Restart();
        before = fixture.Bytes();
        fixture.Run(2);
        Assert.Equal(baseline, fixture.Bytes()[before.Length..]);
        Assert.Empty(fixture.Events());
    }

    [Fact]
    public void Observe_NoHeldOrAnnouncedBindings_CreatesNoLog()
    {
        using var workspace = new ConductorVerbStartTests.Fixture();
        var writer = new ConductEventLogWriter(workspace.Workspace.ConductEventsLogPath);
        var watch = new ConductorProviderHoldWatch(writer);
        var now = DateTimeOffset.Parse("2026-10-17T11:09:00Z");
        Assert.False(File.Exists(writer.CurrentPath));
        watch.ObserveTick([], now);
        watch.ObserveTick([], now.AddMinutes(1));
        Assert.False(File.Exists(writer.CurrentPath));
        Assert.Null(ConductorProviderHoldWatch.For(new ConductorBatchLoop(), null));
    }

    [Fact]
    public void Observe_WriterFailure_ReportsDiagnosticAndDoesNotThrow()
    {
        using var workspace = new ConductorVerbStartTests.Fixture();
        var fixture = new WatchFixture(workspace.Workspace, exhausted: true);
        var failing = new ConductEventLogWriter(Path.Combine(workspace.Workspace.LogDirectory, "failing.log"),
            beforeAppendCommit: () => throw new IOException("fixture write failure"));
        var watch = new ConductorProviderHoldWatch(failing);
        var error = InfrastructureTestSupport.CaptureConsoleError(() => watch.ObserveTick(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal("PROVIDER_HOLD_WATCH_FAILED exception=IOException message=fixture_write_failure" + Environment.NewLine, error);
        Assert.False(File.Exists(failing.CurrentPath));
    }

    private sealed class ClockSource : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-17T11:09:00Z");
    }

    private sealed class WatchFixture
    {
        private readonly OrchestratorWorkspace _workspace;
        private readonly Goal _held;
        private readonly ConductorDriver _driver;
        private readonly Func<AgentOrchestratorKernel, DateTimeOffset, IReadOnlyList<GoalId>>? _probe;
        private readonly Func<int>? _gates;
        internal readonly ClockSource Clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        internal readonly Goal Source;
        internal readonly ConductEventLogWriter Writer;
        internal readonly HashSet<GoalId> Persisted = [];
        internal ConductorBatchLoop Loop { get; private set; }

        internal WatchFixture(OrchestratorWorkspace workspace, bool exhausted,
            Func<AgentOrchestratorKernel, DateTimeOffset, IReadOnlyList<GoalId>>? probe = null, Func<int>? gates = null)
        {
            _probe = probe;
            _gates = gates;
            _workspace = workspace;
            Kernel = new(Clock);
            var agent = ProviderHoldAgentSubstitutionTests.Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.7");
            Source = Kernel.CreateGoal("Source", [new TaskSpec(TaskId.New(), "Verify source", AgentRole.Tester)]);
            Kernel.ActivateGoal(Source.Id, [agent]);
            var task = Source.Tasks.Single();
            var started = Clock.UtcNow.AddMinutes(-2);
            Kernel.RecordTaskDispatch(Source.Id, task.Id, new("grok-cli", "fixture", workspace.ExecutionDirectory, started,
                ProviderName: "xAI", AssignedAgentId: agent.Id.Value));
            if (exhausted)
            {
                const string stderr = "API error (status 402 Payment Required): Grok Build usage balance exhausted";
                var kind = WorkerProviderCatalog.Default().ResolveProfile("grok-cli").ParseOutcome(new(1, "", stderr));
                Kernel.RecordTaskVerification(Source.Id, task.Id, new("fixture", workspace.ExecutionDirectory, 1, "", stderr,
                    Clock.UtcNow.AddMinutes(-1), ProviderFailureKind: kind, DispatchStartedAt: started));
                Kernel.ReportTaskProgress(Source.Id, task.Id, WorkTaskStatus.Failed, "Fixture provider failure.");
                Kernel.EscalateTaskFailure(Source.Id, task.Id, "Fixture provider failure.");
                Assert.Equal(GoalStatus.Failed, Source.Status);
                Assert.Single(ProviderUsageHoldView.Read(Kernel.ProviderHoldScope, Clock.UtcNow));
            }
            _held = InfrastructureTestSupport.MarkGoalRefined(Kernel,
                Kernel.CreateGoal("Held work", [new TaskSpec(TaskId.New(), "Verify held work", AgentRole.Tester)]));
            Kernel.ActivateGoal(_held.Id, [agent]);
            _driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningCount: () => 2,
                dispatchAndStart: _ => throw new InvalidOperationException("The worker cap must prevent dispatch."),
                hasGateReadyGoal: () => false, utcNow: () => Clock.UtcNow);
            Writer = new ConductEventLogWriter(workspace.ConductEventsLogPath, utcNow: () => Clock.UtcNow);
            Loop = NewLoop();
        }

        internal void Succeed(bool verifiedSuccess = false)
        {
            Clock.UtcNow = Clock.UtcNow.AddMinutes(1);
            var task = Source.Tasks.Single();
            Kernel.RecordTaskVerification(Source.Id, task.Id, new("fixture", _workspace.ExecutionDirectory, 0, "Success", "",
                Clock.UtcNow, DispatchStartedAt: task.LastDispatch!.DispatchedAt,
                CompletionVerdictVerifiedSuccess: verifiedSuccess));
            if (verifiedSuccess) Assert.Empty(ProviderUsageHoldView.Read(Kernel.ProviderHoldScope, Clock.UtcNow));
            else Assert.Single(ProviderUsageHoldView.Read(Kernel.ProviderHoldScope, Clock.UtcNow));
        }

        internal void Restart() => Loop = NewLoop();
        private ConductorBatchLoop NewLoop() => new(conductEventLogWriter: Writer, utcNow: () => Clock.UtcNow, workspace: _workspace,
            providerHoldProbe: _probe, countLiveGroupedGates: _gates);
        internal string Run(int ticks) => InfrastructureTestSupport.CaptureConsole(() =>
        {
            var summary = Loop.Run(Kernel, _driver, ConductorAutonomyPolicy.Conservative with { MaxConcurrentPaidWorkers = 2 },
                Path.Combine(_workspace.RootDirectory, "stop"), onlyGoalId: _held.Id.Value, maxIterations: ticks,
                watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false,
                persistGoalTick: (_, ids) => Persisted.UnionWith(ids));
            Assert.Equal(ticks, summary.Ticks);
            Assert.Equal(ticks, summary.Held);
        });

        internal byte[] Bytes() => File.Exists(Writer.CurrentPath) ? File.ReadAllBytes(Writer.CurrentPath) : [];
        internal IReadOnlyList<OwnerConductEvent> Events() => !File.Exists(Writer.CurrentPath) ? [] :
            File.ReadAllLines(Writer.CurrentPath).Select(line => ConductEventFileSource.TryParse(line, out var evt) ? evt : null)
                .OfType<OwnerConductEvent>().Where(evt => evt.EventKind.StartsWith("provider-hold-", StringComparison.Ordinal)).ToArray();
    }
}
