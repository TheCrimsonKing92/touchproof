using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace/database and a fake provider; time is injected.
public sealed class ProviderHoldStandaloneProbeTests
{
    [Fact]
    public void DueWithoutAssignment_ProbesOnceAndRecordsRestoration()
    {
        using var fixture = new Fixture("available");
        fixture.Clock.UtcNow = fixture.Due.AddTicks(-1);
        fixture.Tick();
        Assert.Equal(0, fixture.Calls);
        fixture.Clock.UtcNow = fixture.Due;
        fixture.Tick();
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        var restored = Assert.Single(fixture.Events().Where(evt => evt.EventKind == "provider-hold-restored"));
        Assert.Contains("PROVIDER_HOLD_RESTORED", restored.Detail);
        var note = Assert.Single(fixture.Source.Timeline.Where(evt => ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _)));
        Assert.True(ProviderBudgetHoldSchedules.TryParseStandaloneResult(note, out var result));
        Assert.Equal("available", result.Outcome);
        Assert.Equal(AgentStatus.Offline, fixture.Agent.Status);
    }

    [Theory]
    [InlineData("exhausted", "exhausted")]
    [InlineData("failed", "inconclusive")]
    public void UnavailableOrFault_KeepsHoldAndUsesExistingBackoff(string outcome, string projected)
    {
        using var fixture = new Fixture(outcome);
        fixture.Clock.UtcNow = fixture.Due;
        fixture.Tick();
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
        var row = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal(2, row.Schedule!.Attempt);
        Assert.Equal(fixture.Due.AddMinutes(60), row.Schedule.NextProbeAt);
        Assert.Equal(projected, row.Schedule.LastProbeOutcome);
        Assert.DoesNotContain(fixture.Events(), evt => evt.EventKind == "provider-hold-restored");
        var check = Assert.Single(fixture.Events().Where(evt => evt.EventKind == "provider-hold-check"));
        Assert.Contains($"outcome={outcome}", check.Detail);
        Assert.Contains("reason=fixture-result", check.Detail);
        fixture.Clock.UtcNow = row.Schedule.NextProbeAt;
        fixture.Tick();
        Assert.Equal(2, fixture.Calls);
        var retry = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal(3, retry.Schedule!.Attempt);
        Assert.Equal(fixture.Clock.UtcNow.AddMinutes(120), retry.Schedule.NextProbeAt);
    }

    [Fact]
    public void RunningTaskOnOtherBinding_DefersWithoutConsumingDueTime()
    {
        using var fixture = new Fixture("available");
        var running = fixture.NewGoal();
        var task = running.Tasks.Single();
        fixture.Kernel.RecordTaskDispatch(running.Id, task.Id, new("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory,
            fixture.Clock.UtcNow, ProviderName: "xAI", ClaudeCredentialSourceDirectory: "other-binding"));
        fixture.Kernel.ReportTaskProgress(running.Id, task.Id, WorkTaskStatus.Running, "Running fixture");
        fixture.Clock.UtcNow = fixture.Due;
        fixture.Tick();
        Assert.Equal(0, fixture.Calls);
        fixture.Kernel.RecordTaskVerification(running.Id, task.Id, new("fixture", fixture.Workspace.ExecutionDirectory,
            0, "ok", "", fixture.Clock.UtcNow, DispatchStartedAt: task.LastDispatch!.DispatchedAt));
        fixture.Kernel.ReportTaskProgress(running.Id, task.Id, WorkTaskStatus.Completed, "Done fixture");
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
    }

    [Fact]
    public void AssignedWaitingTask_KeepsProbeOnAdmission()
    {
        using var fixture = new Fixture("available");
        var waiting = fixture.NewGoal();
        var task = waiting.Tasks.Single();
        fixture.WaitingTask = task.Id;
        fixture.Clock.UtcNow = fixture.Due;
        fixture.Tick();
        Assert.Equal(0, fixture.Calls);
        ProviderBudgetProbeAdmission.Apply(fixture.Kernel, waiting, [task.Id], fixture.Preflight, fixture.Clock.UtcNow);
        fixture.Tick();
        Assert.Equal(0, fixture.Calls);
        var evt = Assert.Single(waiting.Timeline.Where(evt => ProviderBudgetHoldSchedules.TryParseProbe(evt, out _)));
        Assert.Equal(task.Id, evt.TaskId);
        Assert.True(ProviderBudgetHoldSchedules.TryParseProbe(evt, out var probe));
        Assert.Equal(fixture.Due, probe.Due);
        Assert.Equal(task.Id, Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow)).Schedule!.AdmittedTaskId);
    }

    [Fact]
    public void ReplacementDuringProbe_CannotBeClearedByStaleSuccess()
    {
        using var fixture = new Fixture("available");
        fixture.DuringProbe = () => { fixture.Clock.UtcNow = fixture.Due.AddMinutes(1); fixture.Exhaust(fixture.NewGoal()); };
        fixture.Clock.UtcNow = fixture.Due;
        fixture.Tick();
        var hold = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow)).Hold;
        Assert.Equal(fixture.Due.AddMinutes(1), hold.PlacedAt);
        Assert.DoesNotContain(fixture.Source.Timeline, evt => ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _));
        Assert.DoesNotContain(fixture.Events(), evt => evt.EventKind == "provider-hold-restored");
    }

    [Fact]
    public async Task RetainedSource_PersistsSuccessAndAttentionProjectionDropsHold()
    {
        using var fixture = new Fixture("available");
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel, TestContext.Current.CancellationToken);
        var kernel = new AgentOrchestratorKernel(fixture.Clock);
        kernel.ProviderHoldHistory.Retain(fixture.Source, kernel.Goals);
        fixture.Clock.UtcNow = fixture.Due;
        var recorder = new ProviderResetRetainedSourceRecorder(() => repository);
        var prober = new ProviderHoldStandaloneProbe(new FakeProbe(() => new("available", "fixture-result")), fixture.Writer,
            fixture.Preflight, recorder.RecordCheck, () => fixture.Clock.UtcNow);
        fixture.Watch.ObserveTick(kernel.ProviderHoldScope, fixture.Clock.UtcNow);
        prober.ObserveTick(kernel, fixture.Clock.UtcNow);
        fixture.Watch.ObserveTick(kernel.ProviderHoldScope, fixture.Clock.UtcNow);
        Assert.Empty(ProviderUsageHoldView.Read(kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Single(fixture.Events().Where(evt => evt.EventKind == "provider-hold-restored"));
        var persisted = await repository.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Empty(ProviderUsageHoldView.Read(persisted.ProviderHoldScope, fixture.Clock.UtcNow));
        var rows = ProviderUsageHoldReader.ReadWorkspace(fixture.Workspace, fixture.Clock.UtcNow);
        Assert.Empty(rows);
        var projection = await repository.ReadAttentionProjectionAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(projection);
        Assert.Empty(ProviderUsageHoldView.ReadEvidence(projection.ProviderEvidence, fixture.Clock.UtcNow));
    }

    [Fact]
    public void ProbeException_IsReportedAndRescheduled()
    {
        using var fixture = new Fixture("available");
        fixture.DuringProbe = () => throw new IOException("fixture fault");
        fixture.Clock.UtcNow = fixture.Due;
        fixture.Tick();
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
        var row = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal(fixture.Due.AddMinutes(60), row.Schedule!.NextProbeAt);
        Assert.Contains(fixture.Events(), evt => evt.Detail.Contains("outcome=failed reason=IOException%3Afixture%20fault", StringComparison.Ordinal));
    }

    [Fact]
    public void Success_ClearsOlderExhaustionsOnSameBinding()
    {
        using var fixture = new Fixture("available");
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        fixture.Exhaust(fixture.NewGoal());
        var row = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal(2, row.Schedule!.Attempt);
        fixture.Clock.UtcNow = row.Schedule.NextProbeAt;
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
    }

    [Fact]
    public void ResetDuringProbe_DiscardsResultFromPreviousDueTime()
    {
        using var fixture = new Fixture("available");
        var reset = fixture.Due.AddDays(1);
        fixture.DuringProbe = () => fixture.Kernel.RecordTaskNote(fixture.Source.Id, fixture.Source.Tasks.Single().Id,
            ProviderBudgetHoldSchedules.FormatReset(new("xai::<provider-default>", reset, "fixture-reset")));
        fixture.Clock.UtcNow = fixture.Due;
        fixture.Tick();
        var row = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal(reset, row.Schedule!.NextProbeAt);
        Assert.DoesNotContain(fixture.Source.Timeline, evt => ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _));
    }

    [Fact]
    public void BusyGate_DefersClaimUntilIdle()
    {
        using var fixture = new Fixture("available");
        var busy = true;
        var calls = 0;
        var prober = new ProviderHoldStandaloneProbe(new FakeProbe(() => { calls++; return new("available", "fixture-result"); }),
            fixture.Writer, fixture.Preflight, providerBusy: () => busy);
        fixture.Clock.UtcNow = fixture.Due;
        prober.ObserveTick(fixture.Kernel, fixture.Clock.UtcNow);
        Assert.Equal(0, calls);
        busy = false;
        prober.ObserveTick(fixture.Kernel, fixture.Clock.UtcNow);
        prober.ObserveTick(fixture.Kernel, fixture.Clock.UtcNow);
        Assert.Equal(1, calls);
    }

    internal sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-17T11:08:00Z");
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly ConductorVerbStartTests.Fixture _workspace = new();
        internal OrchestratorWorkspace Workspace => _workspace.Workspace;
        internal readonly Clock Clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        internal readonly Goal Source;
        internal readonly AgentDefinition Agent = ProviderHoldAgentSubstitutionTests.Scenario.Agent("xai-tester", "xAI", "grok-cli", "grok-4.7") with { Status = AgentStatus.Offline };
        internal readonly ConductEventLogWriter Writer;
        internal readonly ConductorProviderHoldWatch Watch;
        private readonly ProviderHoldStandaloneProbe _prober;
        internal readonly DateTimeOffset Due;
        internal int Calls;
        internal TaskId? WaitingTask;
        internal Action? DuringProbe;
        internal Fixture(string outcome)
        {
            Kernel = new(Clock);
            Source = NewGoal();
            Exhaust(Source);
            Due = Clock.UtcNow.AddMinutes(30);
            Writer = new(Workspace.ConductEventsLogPath, utcNow: () => Clock.UtcNow);
            Watch = new(Writer);
            _prober = new(new FakeProbe(() => { Calls++; DuringProbe?.Invoke(); return new(outcome, "fixture-result"); }),
                Writer, Preflight, utcNow: () => Clock.UtcNow);
        }
        internal Goal NewGoal()
        {
            var goal = Kernel.CreateGoal("Provider hold fixture", [new TaskSpec(TaskId.New(), "Verify fixture", AgentRole.Tester)]);
            Kernel.ActivateGoal(goal.Id, [Agent with { Status = AgentStatus.Available }]);
            return goal;
        }
        internal void Exhaust(Goal goal)
        {
            var task = goal.Tasks.Single();
            var started = Clock.UtcNow.AddSeconds(-1);
            Kernel.RecordTaskDispatch(goal.Id, task.Id, new("grok-cli", "fixture", Workspace.ExecutionDirectory, started,
                ProviderName: "xAI", ModelName: "grok-4.7", AssignedAgentId: Agent.Id.Value));
            Kernel.RecordTaskVerification(goal.Id, task.Id, new("fixture", Workspace.ExecutionDirectory, 1, "",
                "API error (status 402 Payment Required): Grok Build usage balance exhausted", Clock.UtcNow,
                ProviderFailureKind: ProviderFailureKind.BudgetExhausted, DispatchStartedAt: started));
            Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Fixture provider failure");
            Kernel.EscalateTaskFailure(goal.Id, task.Id, "Fixture provider failure");
        }
        internal WorkerSubscriptionPreflightResult Preflight(Goal goal, TaskSpec task, IEnumerable<Goal> scope) =>
            new(task.Id != WaitingTask, "grok-cli", "fixture", task.Id == WaitingTask
                ? [$"blocked: code=provider-budget-exhausted; binding xai::<provider-default> (fixture)"] : []);
        internal void Tick()
        {
            Watch.ObserveTick(Kernel.ProviderHoldScope, Clock.UtcNow);
            _prober.ObserveTick(Kernel, Clock.UtcNow);
            Watch.ObserveTick(Kernel.ProviderHoldScope, Clock.UtcNow);
        }
        internal IReadOnlyList<OwnerConductEvent> Events() => !File.Exists(Writer.CurrentPath) ? [] :
            File.ReadAllLines(Writer.CurrentPath).Select(line => ConductEventFileSource.TryParse(line, out var evt) ? evt : null)
                .OfType<OwnerConductEvent>().ToArray();
        public void Dispose() => _workspace.Dispose();
    }

    private sealed class FakeProbe(Func<ProviderHoldProbeResult> check) : IProviderHoldAvailabilityProbe
    {
        public Task<ProviderHoldProbeResult> CheckAsync(ProviderBudgetExhaustionHold hold, TaskDispatchRecord dispatch,
            CancellationToken cancellationToken) => Task.FromResult(check());
    }
}
