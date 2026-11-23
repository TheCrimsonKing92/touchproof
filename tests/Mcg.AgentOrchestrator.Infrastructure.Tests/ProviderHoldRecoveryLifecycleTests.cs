using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ProviderHoldDispatchScenarioTests;

// Parallel-safe: isolated event logs, fixed clocks and an injected availability probe.
public sealed class ProviderHoldRecoveryLifecycleTests
{
    [Fact]
    public async Task ProvenCredentialAndModelScopesSurviveTogether()
    {
        using var workspace = new ConductorVerbStartTests.Fixture();
        var clock = new ProviderHoldScenarioClock();
        var kernel = new AgentOrchestratorKernel(clock);
        const string first = @"C:\profiles\first";
        const string second = @"C:\profiles\second";
        var sonnet = Record(kernel, clock, "You've hit your sonnet limit", "", first);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var opus = Record(kernel, clock, "You've hit your opus limit", "", first, "claude-opus-4-6");
        var candidate = NewGoal(kernel);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.Workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        var restored = await repository.LoadAsync();

        Assert.Equal(2, ProviderUsageHoldView.Read(restored.Goals, clock.UtcNow).Count);
        var sparse = await repository.ReadProviderHoldSeedEvidenceAsync([sonnet.Id, opus.Id]);
        Assert.Equal(2, ProviderUsageHoldView.ReadEvidence(sparse!, clock.UtcNow).Count);
        Assert.Contains("model=claude-sonnet-4-6", Assert.Single(Findings(restored, restored.GetGoal(candidate.Id), first)));
        Assert.Contains("model=claude-opus-4-6", Assert.Single(Findings(restored, restored.GetGoal(candidate.Id), first, "claude-opus-4-6")));
        Assert.Empty(Findings(restored, restored.GetGoal(candidate.Id), first, "claude-haiku-4-5"));
        Assert.Empty(Findings(restored, restored.GetGoal(candidate.Id), second));
    }

    [Fact]
    public void ApiAndSubscriptionRoutesOnTheSameDirectoryAreIndependent()
    {
        var clock = new ProviderHoldScenarioClock();
        var kernel = new AgentOrchestratorKernel(clock);
        const string credential = @"C:\profiles\login";
        Record(kernel, clock, "You've hit your weekly limit", "", credential);
        Record(kernel, clock, "You've hit your weekly limit", "", credential, apiRoute: "api-fixture-a");
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        var candidate = NewGoal(restored);

        Assert.Equal(2, ProviderUsageHoldView.Read(restored.Goals, clock.UtcNow).Count);
        Assert.Contains(credential.ToLowerInvariant(), Assert.Single(Findings(restored, candidate, credential)));
        Assert.Contains("api-fixture-a", Assert.Single(Findings(restored, candidate, "api-fixture-a")));
        Assert.Empty(Findings(restored, candidate, "api-fixture-b"));
    }

    [Fact]
    public void CoincidentBindingTextDoesNotShareACooldown()
    {
        var clock = new ProviderHoldScenarioClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var source = Record(kernel, clock, SubscriptionCooldownSubstitutionTests.Scenario.FableLimit, "", null,
            model: "claude-fable-5-2");
        var candidate = NewGoal(kernel).Tasks.Single().Id;

        Assert.True(HasCooldown(null));
        Assert.False(HasCooldown("<provider-default>"));

        bool HasCooldown(string? credential) => ProviderLimitSignals.TryGetProviderSubscriptionCooldown(
            source, candidate, "Anthropic", "claude-fable-5-2", clock.UtcNow, out _,
            new("Anthropic", credential, null));
    }

    [Fact]
    public void CoincidentModelHoldTextDoesNotShareAMemoizedHold()
    {
        var clock = new ProviderHoldScenarioClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var provider = Record(kernel, clock, "You've hit your sonnet limit", "", null);
        var credential = Record(kernel, clock, "You've hit your sonnet limit", "", "<provider-default>");
        var providerBinding = new ProviderHoldBinding("Anthropic", null, "CLAUDE-SONNET-4-6");
        var credentialBinding = new ProviderHoldBinding("Anthropic", "<provider-default>", "claude-sonnet-4-6");
        var memo = new ProviderBudgetHoldMemo(kernel.Goals);

        Assert.Equal(providerBinding.Key, credentialBinding.Key);
        Assert.True(memo.TryGetExact(providerBinding, out var providerHold));
        Assert.True(memo.TryGetExact(credentialBinding, out var credentialHold));
        Assert.Equal(provider.Id, providerHold.SourceGoalId);
        Assert.Equal(credential.Id, credentialHold.SourceGoalId);
        Assert.True(memo.TryGetExact(credentialBinding with { Model = "CLAUDE-SONNET-4-6" }, out var repeated));
        Assert.Same(credentialHold, repeated);
        Assert.Equal(2, memo.EvaluationCount);
    }

    [Fact]
    public void AdmissionAndSubstitutionUseTheHoldSelectedByPreflight()
    {
        var clock = new ProviderHoldScenarioClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var provider = Record(kernel, clock, "", "ERROR: weekly usage limit reached", null);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var credential = Record(kernel, clock, "", "ERROR: weekly usage limit reached", "<provider-default>");
        var goal = NewGoal(kernel);
        var task = goal.Tasks.Single();
        var primary = ProviderHoldAgentSubstitutionTests.Scenario.Agent("claude-tester", "Anthropic", "claude-cli", "claude-sonnet-4-6");
        var alternate = ProviderHoldAgentSubstitutionTests.Scenario.Agent("openai-tester", "OpenAI", "codex-cli", "gpt-6.1-sol");
        clock.UtcNow = clock.UtcNow.AddMinutes(30);
        var blocked = Preflight(goal, task, kernel.ProviderHoldScope);
        var hold = Assert.IsType<ProviderBudgetExhaustionHold>(blocked.BlockedProviderHold);
        Assert.Equal(credential.Id, hold.SourceGoalId);
        var other = Assert.Single(ProviderUsageHoldView.Read(kernel.ProviderHoldScope, clock.UtcNow),
            row => row.Hold.SourceGoalId == provider.Id).Hold;
        Assert.Equal(other.BindingKey, hold.BindingKey);
        Assert.False(ProviderBudgetProbeAdmission.WaitsOnBinding(blocked, other));
        var resolution = new ProviderHoldAgentSubstitution(kernel, [primary, alternate], Preflight, () => clock.UtcNow)
            .Resolve(goal, task);
        Assert.StartsWith($"{hold.BindingKey}|{credential.Id}|", Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(resolution).HoldIdentity);

        ProviderBudgetProbeAdmission.Apply(kernel, goal, [task.Id], Preflight, clock.UtcNow);
        var note = Assert.Single(goal.Timeline, evt => ProviderBudgetHoldSchedules.TryParseProbe(evt, out _));
        Assert.True(ProviderBudgetHoldSchedules.TryParseProbe(note, out var marker));
        Assert.Equal(hold.BindingIdentity, marker.BindingIdentity);
        Assert.Equal(task.Id, ProviderBudgetHoldSchedules.Evaluate(kernel.ProviderHoldScope, hold, clock.UtcNow).AdmittedTaskId);
        Assert.Null(ProviderBudgetHoldSchedules.Evaluate(kernel.ProviderHoldScope, other, clock.UtcNow).AdmittedTaskId);

        WorkerSubscriptionPreflightResult Preflight(Goal current, TaskSpec assigned, IEnumerable<Goal> scope)
        {
            if (assigned.AssignedAgentId == alternate.Id) return new(true, "codex-cli", "fixture", []);
            var findings = new List<string>();
            var blockedHold = WorkerSubscriptionPreflightFindings.AddProviderHoldFindings(
                findings, scope, "Anthropic", "<provider-default>", "claude-sonnet-4-6", current, assigned, clock.UtcNow);
            return new(blockedHold is null, "claude-cli", "fixture", findings,
                blockedHold is null ? null : WorkerProfileDispatcher.ProviderBudgetExhaustionErrorCode)
                { BlockedProviderHold = blockedHold };
        }
    }

    [Fact]
    public void FailedCheckSurvivesRestartAndAvailableCheckUnblocksOneRetry()
    {
        using var workspace = new ConductorVerbStartTests.Fixture();
        var clock = new ProviderHoldScenarioClock();
        var kernel = new AgentOrchestratorKernel(clock);
        const string credential = @"C:\private\login";
        var source = Record(kernel, clock, "", "ERROR: 401 invalid credentials", credential);
        var taskId = source.Tasks.Single().Id;
        kernel.RetryTaskAutomatically(source.Id, taskId, "Fixture requeues held work", RetryCause.ProviderInterruption);
        var initial = Assert.Single(ProviderUsageHoldView.Read(kernel.Goals, clock.UtcNow));
        var writer = new ConductEventLogWriter(workspace.Workspace.ConductEventsLogPath);
        var watch = new ConductorProviderHoldWatch(writer);
        watch.ObserveTick(kernel.Goals, clock.UtcNow);
        var probe = new TestProbe();
        var standalone = CreateProbe();
        clock.UtcNow = initial.Schedule!.NextProbeAt.AddSeconds(-1);
        Assert.Empty(standalone.ObserveTick(kernel, clock.UtcNow));
        Assert.Equal(0, probe.Calls);

        clock.UtcNow = initial.Schedule.NextProbeAt;
        Assert.Equal(source.Id, Assert.Single(standalone.ObserveTick(kernel, clock.UtcNow)));
        Assert.Empty(standalone.ObserveTick(kernel, clock.UtcNow));
        Assert.Equal(1, probe.Calls);
        var held = Assert.Single(ProviderUsageHoldView.Read(kernel.Goals, clock.UtcNow));
        Assert.Equal("inconclusive", held.Schedule!.LastProbeOutcome);
        var restarted = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), clock);
        Assert.Equal(held, Assert.Single(ProviderUsageHoldView.Read(restarted.Goals, clock.UtcNow)));
        Assert.Single(Findings(restarted, restarted.GetGoal(source.Id), credential));
        Assert.Empty(CreateProbe().ObserveTick(restarted, clock.UtcNow));
        Assert.Equal(1, probe.Calls);

        clock.UtcNow = held.Schedule.NextProbeAt;
        probe.Result = new("available", "fixture reply");
        Assert.Equal(source.Id, Assert.Single(CreateProbe().ObserveTick(restarted, clock.UtcNow)));
        Assert.Empty(CreateProbe().ObserveTick(restarted, clock.UtcNow));
        Assert.Equal(2, probe.Calls);
        watch.ObserveTick(restarted.Goals, clock.UtcNow);
        var recovered = AgentOrchestratorKernel.FromSnapshot(restarted.ExportSnapshot(), clock);
        var waiting = recovered.GetTask(source.Id, taskId);
        Assert.Equal(WorkTaskStatus.Assigned, waiting.Status);
        Assert.Empty(Findings(recovered, recovered.GetGoal(source.Id), credential));

        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        recovered.RecordTaskDispatch(source.Id, taskId, waiting.DispatchHistory.Single() with { DispatchedAt = clock.UtcNow });
        new ConductorProviderHoldWatch(writer).ObserveTick(recovered.Goals, clock.UtcNow);
        new ConductorProviderHoldWatch(writer).ObserveTick(recovered.Goals, clock.UtcNow);
        Assert.Equal(2, waiting.DispatchHistory.Count);
        Assert.Empty(waiting.RetryAdmissionHistory);
        var events = File.ReadLines(writer.CurrentPath).Select(line => ConductEventFileSource.TryParse(line, out var evt) ? evt : null)
            .OfType<OwnerConductEvent>().ToArray();
        Assert.Single(events, evt => evt.EventKind == "provider-hold-restored");
        var redispatch = Assert.Single(events, evt => evt.EventKind == "provider-hold-redispatched");
        Assert.Equal(taskId.Value, OwnerEventFields.Field(redispatch, "task"));
        Assert.DoesNotContain(credential, redispatch.Detail, StringComparison.OrdinalIgnoreCase);

        ProviderHoldStandaloneProbe CreateProbe() => new(probe, writer,
            (_, _, _) => new(true, "claude-cli", "fixture", []), utcNow: () => clock.UtcNow);
    }

    [Fact]
    public void RedispatchAfterStandaloneRecoverySkipsAnEarlierInconclusiveTaskProbe()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var waiting = fixture.NewGoal();
        var task = waiting.Tasks.Single();
        fixture.WaitingTask = task.Id;
        fixture.Watch.ObserveTick(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow);
        fixture.Clock.UtcNow = fixture.Due;
        ProviderBudgetProbeAdmission.Apply(fixture.Kernel, waiting, [task.Id], fixture.Preflight, fixture.Clock.UtcNow);
        var dispatch = new TaskDispatchRecord("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory,
            fixture.Clock.UtcNow, ProviderName: "xAI");
        fixture.Kernel.RecordTaskDispatch(waiting.Id, task.Id, dispatch);
        Assert.True(ProviderBudgetHoldSchedules.IsAdmittedProbe(waiting, task, dispatch));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        fixture.Kernel.RecordDispatchExecutionResult(waiting.Id, task.Id,
            new(dispatch.Command, dispatch.WorkingDirectory, 1, "", "Provider process timed out", fixture.Clock.UtcNow,
                DispatchStartedAt: dispatch.DispatchedAt));
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        var held = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal("inconclusive", held.Schedule!.LastProbeOutcome);

        fixture.WaitingTask = null;
        fixture.Clock.UtcNow = held.Schedule.NextProbeAt;
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
        Assert.Single(fixture.Events(), evt => evt.EventKind == "provider-hold-restored");
        Assert.DoesNotContain(fixture.Events(), evt => evt.EventKind == "provider-hold-redispatched");

        var restarted = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot(), fixture.Clock);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        restarted.RecordTaskDispatch(waiting.Id, task.Id, dispatch with { DispatchedAt = fixture.Clock.UtcNow });
        new ConductorProviderHoldWatch(fixture.Writer).ObserveTick(restarted.ProviderHoldScope, fixture.Clock.UtcNow);
        new ConductorProviderHoldWatch(fixture.Writer).ObserveTick(restarted.ProviderHoldScope, fixture.Clock.UtcNow);
        var redispatch = Assert.Single(fixture.Events(), evt => evt.EventKind == "provider-hold-redispatched");
        Assert.Equal(task.Id.Value, OwnerEventFields.Field(redispatch, "task"));
        Assert.Equal(ProviderBudgetHoldSchedules.FormatInstant(fixture.Clock.UtcNow), OwnerEventFields.Field(redispatch, "dispatched_at"));
    }

    [Fact]
    public void AdmittedTaskProbe_PreservesDirtyWorktreeRecovery()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var waiting = fixture.NewGoal();
        var task = waiting.Tasks.Single();
        fixture.WaitingTask = task.Id;
        fixture.Clock.UtcNow = fixture.Due;
        ProviderBudgetProbeAdmission.Apply(fixture.Kernel, waiting, [task.Id], fixture.Preflight, fixture.Clock.UtcNow);
        var dispatch = new TaskDispatchRecord("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory,
            fixture.Clock.UtcNow, ProviderName: "xAI");
        fixture.Kernel.RecordTaskDispatch(waiting.Id, task.Id, dispatch);
        Assert.True(ProviderBudgetHoldSchedules.IsAdmittedProbe(waiting, task, dispatch));

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        fixture.Kernel.RecordDispatchExecutionResult(waiting.Id, task.Id,
            new(dispatch.Command, dispatch.WorkingDirectory, 1, "",
                "Developer/Tester dispatch exited 0 but left the worktree dirty. status_short= M Fixture.cs.",
                fixture.Clock.UtcNow, DispatchStartedAt: dispatch.DispatchedAt));

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.True(DirtyDispatchEvidence.TryBuildDirtyDispatchRecovery(task, out var recovery));
        Assert.Contains("M Fixture.cs", recovery.ChangedFiles);
        var recorded = Assert.IsType<TaskVerificationRecord>(task.LastVerification);
        Assert.Equal(TaskOutcomeRules.DirtyDispatchRecovery.Token, recorded.CompletionVerdictRule);
        Assert.Equal(DispatchOutcomeKind.DirtyWorktreeRecoverable,
            DispatchFailureClassifier.Classify(task, recorded).Kind);
        Assert.Equal(DispatchOutcomeKind.DirtyWorktreeRecoverable,
            DispatchFailureClassifier.ClassifyStoredRound(task, recorded).Kind);
        var gate = Assert.Single(fixture.Kernel.BuildVerificationGate(waiting.Id).Tasks);
        Assert.Equal(VerificationGateStatus.FailedVerification, gate.GateStatus);
        Assert.Equal(VerificationGateReason.DirtyUnverifiedRecovery, gate.Reason);
    }

    [Fact]
    public void AdmittedTaskProbe_PreservesSandboxRefusal()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var waiting = fixture.NewGoal();
        var task = waiting.Tasks.Single();
        fixture.WaitingTask = task.Id;
        fixture.Clock.UtcNow = fixture.Due;
        ProviderBudgetProbeAdmission.Apply(fixture.Kernel, waiting, [task.Id], fixture.Preflight, fixture.Clock.UtcNow);
        var dispatch = new TaskDispatchRecord("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory,
            fixture.Clock.UtcNow, ProviderName: "xAI");
        fixture.Kernel.RecordTaskDispatch(waiting.Id, task.Id, dispatch);
        Assert.True(ProviderBudgetHoldSchedules.IsAdmittedProbe(waiting, task, dispatch));
        var previousRetryAt = task.LatestRetryAt;

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        fixture.Kernel.RecordDispatchExecutionResult(waiting.Id, task.Id,
            new(dispatch.Command, dispatch.WorkingDirectory, 1, "",
                $"WORKER_SANDBOX_REFUSED reason=node-dependency-preparation-failed path={dispatch.WorkingDirectory}",
                fixture.Clock.UtcNow, DispatchStartedAt: dispatch.DispatchedAt));

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(previousRetryAt, task.LatestRetryAt);
        var recorded = Assert.IsType<TaskVerificationRecord>(task.LastVerification);
        Assert.Equal(TaskOutcomeRules.WorkerSandboxRefused.Token, recorded.CompletionVerdictRule);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, DispatchFailureClassifier.Classify(task, recorded).RecoveryRecommendation);
    }

    [Fact]
    public void SignInCheckKeepsQuotaHeldAndRefusedRecheckRefreshesProviderReset()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("authentication-required");
        fixture.Clock.UtcNow = fixture.Due;
        fixture.Tick();
        var row = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.Goals, fixture.Clock.UtcNow));
        Assert.Equal(ProviderFailureKind.BudgetExhausted, row.Hold.Reason);
        Assert.Equal(ProviderFailureKind.AuthenticationRequired, row.Schedule!.LastActionableFailure);
        var reset = row.Schedule.NextProbeAt.AddDays(1);
        var probe = new TestProbe { Result = new("exhausted", "new quota window") { ProviderResetAt = reset } };
        fixture.Clock.UtcNow = row.Schedule.NextProbeAt;
        new ProviderHoldStandaloneProbe(probe, fixture.Writer, fixture.Preflight).ObserveTick(fixture.Kernel, fixture.Clock.UtcNow);
        row = Assert.Single(ProviderUsageHoldView.Read(fixture.Kernel.Goals, fixture.Clock.UtcNow));
        Assert.Equal(reset, row.ProviderResetAt);
        Assert.Equal(reset, row.Schedule!.NextProbeAt);
        Assert.Equal(1, probe.Calls);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void AuthenticationFailureReturnsToTheSchedulerAfterOneSuccessfulCheck()
    {
        using var scenario = new ProviderUsageHoldSelfRetryScenarioTests.Scenario();
        var source = scenario.Incident(stderr: "API Error: 401 invalid credentials");
        Assert.Equal(GoalStatus.Failed, source.Status);
        Assert.Equal(ProviderFailureKind.AuthenticationRequired, scenario.Hold.Reason);
        scenario.At("2026-10-17T11:09:00Z");
        Assert.IsNotType<ConductorAdvanceOutcome.Escalated>(scenario.Tick(source).Outcome);
        Assert.Equal(WorkTaskStatus.Assigned, scenario.Current(source).Tasks.Single().Status);
        Assert.Contains("sign in", scenario.BlockedFinding(source));
        Assert.Equal(1, scenario.PrimaryDispatches());
        var probe = new TestProbe { Result = new("available", "fixture reply") };
        var standalone = new ProviderHoldStandaloneProbe(probe, new ConductEventLogWriter(scenario.Workspace.ConductEventsLogPath),
            scenario.Preflight, utcNow: () => scenario.Clock.UtcNow);
        scenario.Clock.UtcNow = ProviderBudgetHoldSchedules.Evaluate(scenario.Kernel.ProviderHoldScope, scenario.Hold, scenario.Clock.UtcNow).NextProbeAt;
        Assert.Equal(source.Id, Assert.Single(standalone.ObserveTick(scenario.Kernel, scenario.Clock.UtcNow)));
        Assert.Empty(standalone.ObserveTick(scenario.Kernel, scenario.Clock.UtcNow));
        Assert.Equal(1, probe.Calls);

        scenario.Tick(source);
        scenario.AssertPrimary(source, probe: false);
        scenario.Tick(source);
        Assert.Equal(2, scenario.PrimaryDispatches());
        Assert.Equal([RetryCause.ProviderInterruption], scenario.Causes);
        Assert.Empty(scenario.Escalations);
    }

    private sealed class TestProbe : IProviderHoldAvailabilityProbe
    {
        internal int Calls { get; private set; }
        internal ProviderHoldProbeResult Result { get; set; } = new("failed", "timeout");
        public Task<ProviderHoldProbeResult> CheckAsync(ProviderBudgetExhaustionHold hold, TaskDispatchRecord dispatch,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
}
