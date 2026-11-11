using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace, fake clock and synchronous event callbacks; no live provider.
public sealed class ProviderResetGenerationTests
{
    private const string Binding = "xai::<provider-default>";

    [Theory]
    [InlineData("failed")]
    [InlineData("exhausted")]
    [InlineData("authentication-required")]
    public void NewReset_AfterHistoryStartsFreshBoundedBackoff(string outcome)
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture(outcome);
        for (var i = 0; i < 6; i++)
        {
            var before = Row(fixture);
            fixture.Clock.UtcNow = before.Schedule!.NextProbeAt;
            fixture.Tick();
        }
        Assert.Equal(7, Row(fixture).Schedule!.Attempt);
        var previousDiagnosisAt = Row(fixture).Schedule!.LastActionableFailureAt;
        var resetAt = fixture.Clock.UtcNow.AddSeconds(-1);
        Reset(fixture, "new-reset", resetAt);
        var fresh = Row(fixture);
        Assert.Equal(1, fresh.Schedule!.Attempt);
        Assert.Equal(resetAt, fresh.Schedule.NextProbeAt);
        Assert.Equal(resetAt, fresh.OverdueSince);
        Assert.Equal("new-reset", fresh.Schedule.ResetIntentId);
        Assert.Equal(previousDiagnosisAt, fresh.Schedule.LastActionableFailureAt);
        Assert.Equal(6, fixture.Calls);
        fixture.Tick();
        var later = Row(fixture).Schedule!;
        Assert.Equal(7, fixture.Calls);
        Assert.Equal(2, later.Attempt);
        Assert.Equal(fixture.Clock.UtcNow.AddMinutes(60), later.NextProbeAt);
        Assert.Equal("new-reset", later.ResetIntentId);
        // Replaying the original persisted intent cannot erase later evidence or reset backoff.
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        Reset(fixture, "new-reset", resetAt);
        Assert.Equal(later, Row(fixture).Schedule);
    }

    [Fact]
    public void ResetDuringProbe_SameDueAndAttemptRejectsOldGeneration()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        fixture.Clock.UtcNow = fixture.Due;
        fixture.DuringProbe = () => Reset(fixture, "replacement", fixture.Due);
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
        var row = Row(fixture);
        Assert.Equal(1, row.Schedule!.Attempt);
        Assert.Equal(fixture.Due, row.Schedule.NextProbeAt);
        Assert.Equal("replacement", row.Schedule.ResetIntentId);
        Assert.DoesNotContain(fixture.Source.Timeline,
            evt => ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _));
        Assert.Contains(fixture.Events(), evt => evt.Detail.Contains("stale-probe-result", StringComparison.Ordinal));
        fixture.DuringProbe = null;
        fixture.Tick();
        Assert.Equal(2, fixture.Calls);
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
    }

    [Theory]
    [InlineData(0, ProviderFailureKind.Unknown)]
    [InlineData(1, ProviderFailureKind.BudgetExhausted)]
    [InlineData(1, ProviderFailureKind.AuthenticationRequired)]
    public void ResetDuringTaskProbe_PreservesOwnershipAndRejectsLateResult(
        int exitCode, ProviderFailureKind kind)
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var waiting = fixture.NewGoal();
        var task = waiting.Tasks.Single();
        fixture.WaitingTask = task.Id;
        fixture.Clock.UtcNow = fixture.Due;
        ProviderBudgetProbeAdmission.Apply(fixture.Kernel, waiting, [task.Id], fixture.Preflight, fixture.Clock.UtcNow);
        fixture.Kernel.RecordTaskDispatch(waiting.Id, task.Id,
            new("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory, fixture.Clock.UtcNow, ProviderName: "xAI"));
        Reset(fixture, "replacement", fixture.Due);
        Assert.True(Row(fixture).Schedule!.ProbeInFlight);
        fixture.Tick();
        Assert.Equal(0, fixture.Calls);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        CompleteTaskProbe(fixture, waiting, kind, success: exitCode == 0);
        if (exitCode != 0) Assert.Equal(WorkTaskStatus.Failed, task.Status);
        var row = Row(fixture);
        Assert.False(row.Schedule!.ProbeInFlight);
        Assert.Equal(1, row.Schedule.Attempt);
        Assert.Equal(fixture.Due, row.Schedule.NextProbeAt);
        fixture.WaitingTask = null;
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
    }

    [Theory]
    [InlineData(ProviderFailureKind.Unknown, true)]
    [InlineData(ProviderFailureKind.AuthenticationRequired, false)]
    [InlineData(ProviderFailureKind.BudgetExhausted, false)]
    public void StaleExhaustion_DoesNotInvalidateNextCurrentTaskProbe(
        ProviderFailureKind currentKind, bool success)
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        fixture.Clock.UtcNow = fixture.Due;
        var stale = StartTaskProbe(fixture);
        Reset(fixture, "replacement", fixture.Due);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        CompleteTaskProbe(fixture, stale, ProviderFailureKind.BudgetExhausted);
        Assert.Equal(1, Row(fixture).Schedule!.Attempt);
        Assert.Equal(fixture.Source.Id, Row(fixture).Hold.SourceGoalId);

        var current = StartTaskProbe(fixture);
        var marker = current.Timeline.Single(evt => ProviderBudgetHoldSchedules.TryParseProbe(evt, out _));
        Assert.True(ProviderBudgetHoldSchedules.TryParseProbe(marker, out var admitted));
        Assert.Equal("replacement", admitted.ResetIntentId);
        Assert.True(Row(fixture).Schedule!.ProbeInFlight);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        CompleteTaskProbe(fixture, current, currentKind, success);
        if (success)
            Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        else
        {
            var schedule = Row(fixture).Schedule!;
            Assert.False(schedule.ProbeInFlight);
            Assert.Equal(2, schedule.Attempt);
            Assert.Equal(fixture.Clock.UtcNow.AddMinutes(60), schedule.NextProbeAt);
            Assert.Equal(currentKind, schedule.LastActionableFailure);
        }
        Assert.Equal(0, fixture.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ValidRecovery_ThenNewExhaustionStartsFreshEpisode(bool standalone, bool reset)
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        fixture.Clock.UtcNow = fixture.Due;
        if (reset) Reset(fixture, "recovered-generation", fixture.Due);
        if (standalone) fixture.Tick();
        else
        {
            var recovery = StartTaskProbe(fixture);
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            CompleteTaskProbe(fixture, recovery, ProviderFailureKind.Unknown, success: true);
        }
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        var next = fixture.NewGoal();
        fixture.Exhaust(next);
        var row = Row(fixture);
        Assert.Equal(next.Id, row.Hold.SourceGoalId);
        Assert.Equal(1, row.Schedule!.Attempt);
        Assert.Null(row.Schedule.ResetIntentId);
        Assert.Equal(fixture.Clock.UtcNow.AddMinutes(30), row.Schedule.NextProbeAt);
        Assert.Equal(ProviderFailureKind.BudgetExhausted, row.Schedule.LastActionableFailure);
        Assert.Equal(fixture.Clock.UtcNow, row.Schedule.LastActionableFailureAt);
    }

    [Fact]
    public async Task EvictedProbeGoals_RetainDiagnosisAndBackoffAcrossRestart()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath);
        foreach (var kind in new[] { ProviderFailureKind.AuthenticationRequired, ProviderFailureKind.Unknown })
        {
            fixture.Clock.UtcNow = Row(fixture).Schedule!.NextProbeAt;
            var probe = StartTaskProbe(fixture);
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            CompleteTaskProbe(fixture, probe, kind);
            fixture.Kernel.CancelGoal(probe.Id, "Keep completed probe evidence without an active goal.");
            var before = Row(fixture);
            await repository.SaveAsync(fixture.Kernel, TestContext.Current.CancellationToken);
            Assert.Equal(1, fixture.Kernel.EvictTerminalGoalAggregates([probe.Id]));
            Assert.Contains(fixture.Kernel.ProviderHoldHistory.Retained, goal => goal.Id == probe.Id);
            Assert.Equal(before, Row(fixture));

            // The live source is loaded normally; terminal probe goals are seeded from the index.
            var restarted = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot(), fixture.Clock);
            ProviderHoldHistorySeed.Apply(restarted, repository, _ => { });
            var restored = Assert.Single(ProviderUsageHoldView.Read(restarted.ProviderHoldScope, fixture.Clock.UtcNow));
            Assert.Equal(before, restored);
            Assert.DoesNotContain(restarted.Goals, goal => goal.Id == probe.Id);
            Assert.Equal(ProviderFailureKind.AuthenticationRequired, restored.Schedule!.LastActionableFailure);
        }
        var final = Row(fixture);
        Assert.Equal(3, final.Schedule!.Attempt);
        Assert.Equal("inconclusive", final.Schedule.LastProbeOutcome);
        Assert.Contains("last confirmed: authentication-required", final.Diagnosis);
        Assert.Contains("recovery: sign in", final.Diagnosis);
    }

    [Fact]
    public void ReplacementDuringTaskProbe_RejectsSuccessFromOldHold()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var waiting = fixture.NewGoal();
        var task = waiting.Tasks.Single();
        fixture.WaitingTask = task.Id;
        fixture.Clock.UtcNow = fixture.Due;
        ProviderBudgetProbeAdmission.Apply(fixture.Kernel, waiting, [task.Id], fixture.Preflight, fixture.Clock.UtcNow);
        fixture.Kernel.RecordTaskDispatch(waiting.Id, task.Id,
            new("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory, fixture.Clock.UtcNow, ProviderName: "xAI"));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        var replacement = fixture.NewGoal();
        fixture.Exhaust(replacement);
        var placed = fixture.Clock.UtcNow;
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        fixture.Kernel.RecordTaskVerification(waiting.Id, task.Id,
            new("fixture", fixture.Workspace.ExecutionDirectory, 0, "AVAILABLE", "", fixture.Clock.UtcNow,
                DispatchStartedAt: fixture.Due, CompletionVerdictVerifiedSuccess: true));
        var row = Row(fixture);
        Assert.Equal(replacement.Id, row.Hold.SourceGoalId);
        Assert.Equal(placed, row.Hold.PlacedAt);
        Assert.False(row.Schedule!.ProbeInFlight);
    }

    [Theory]
    [InlineData(ProviderFailureKind.Unknown, "inconclusive")]
    [InlineData(ProviderFailureKind.AuthenticationRequired, "authentication-required")]
    [InlineData(ProviderFailureKind.BudgetExhausted, "exhausted")]
    public void CurrentTaskProbe_AdvancesOnlyTheNewGenerationBackoff(
        ProviderFailureKind kind, string expected)
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var waiting = fixture.NewGoal();
        var task = waiting.Tasks.Single();
        fixture.WaitingTask = task.Id;
        fixture.Clock.UtcNow = fixture.Due;
        Reset(fixture, "current", fixture.Due);
        ProviderBudgetProbeAdmission.Apply(fixture.Kernel, waiting, [task.Id], fixture.Preflight, fixture.Clock.UtcNow);
        var marker = Assert.Single(waiting.Timeline.Where(evt => ProviderBudgetHoldSchedules.TryParseProbe(evt, out _)));
        Assert.True(ProviderBudgetHoldSchedules.TryParseProbe(marker, out var admitted));
        Assert.Equal("current", admitted.ResetIntentId);
        Assert.Equal(Row(fixture).Hold.PlacedAt, admitted.HoldPlacedAt);
        fixture.Kernel.RecordTaskDispatch(waiting.Id, task.Id,
            new("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory, fixture.Clock.UtcNow, ProviderName: "xAI"));
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        fixture.Kernel.RecordTaskVerification(waiting.Id, task.Id,
            new("fixture", fixture.Workspace.ExecutionDirectory, 1, "", "fixture", fixture.Clock.UtcNow,
                ProviderFailureKind: kind, DispatchStartedAt: fixture.Due));
        var row = Row(fixture);
        Assert.Equal(2, row.Schedule!.Attempt);
        Assert.Equal(expected, row.Schedule.LastProbeOutcome);
        Assert.Equal(fixture.Clock.UtcNow.AddMinutes(60), row.Schedule.NextProbeAt);
        Assert.Equal("current", row.Schedule.ResetIntentId);
    }

    [Fact]
    public async Task NewerReset_IsolatesBindingAndPreservesFutureDueAgainstOldIntent()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var other = fixture.NewGoal();
        var task = other.Tasks.Single();
        fixture.Kernel.RecordTaskDispatch(other.Id, task.Id, new("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory,
            fixture.Clock.UtcNow.AddSeconds(-1), ProviderName: "xAI", ClaudeCredentialSourceDirectory: "other"));
        fixture.Kernel.RecordTaskVerification(other.Id, task.Id, new("fixture", fixture.Workspace.ExecutionDirectory, 1,
            "", "API error (status 402 Payment Required): Grok Build usage balance exhausted", fixture.Clock.UtcNow,
            ProviderFailureKind: ProviderFailureKind.BudgetExhausted, DispatchStartedAt: fixture.Clock.UtcNow.AddSeconds(-1)));
        var otherBefore = ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow)
            .Single(row => row.Hold.BindingKey != Binding).Schedule;
        fixture.Clock.UtcNow = fixture.Due;
        var issued = fixture.Clock.UtcNow;
        var future = issued.AddDays(1);
        Reset(fixture, "newer", future, issued);
        // An older queued intent can arrive later without superseding the accepted newer intent.
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        Reset(fixture, "older", fixture.Clock.UtcNow, issued.AddMinutes(-1));
        Assert.Equal(future, Row(fixture).Schedule!.NextProbeAt);
        Assert.Equal("newer", Row(fixture).Schedule!.ResetIntentId);
        Assert.Null(Row(fixture).OverdueSince);
        Assert.Equal(otherBefore, ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow)
            .Single(row => row.Hold.BindingKey != Binding).Schedule);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel, TestContext.Current.CancellationToken);
        var restored = await repository.LoadAsync(TestContext.Current.CancellationToken);
        var resetEvents = fixture.Source.Timeline.Where(evt =>
            ProviderBudgetHoldSchedules.TryParseReset(evt, out _)).ToArray();
        Assert.Equal(2, resetEvents.Length);
        var restoredResetEvents = restored.GetGoal(fixture.Source.Id).Timeline.Where(evt =>
            ProviderBudgetHoldSchedules.TryParseReset(evt, out _)).ToArray();
        Assert.Equal(resetEvents, restoredResetEvents);
        Assert.Equal(issued, restoredResetEvents[0].OperatorIntentCreatedAt);
        Assert.Equal(issued.AddMinutes(-1), restoredResetEvents[1].OperatorIntentCreatedAt);
        Assert.Equal(Row(fixture).Schedule, ProviderUsageHoldView.Read(restored.ProviderHoldScope, fixture.Clock.UtcNow)
            .Single(row => row.Hold.BindingKey == Binding).Schedule);
        var projection = await repository.ReadAttentionProjectionAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(projection);
        var projected = ProviderUsageHoldView.ReadEvidence(projection.ProviderEvidence, fixture.Clock.UtcNow)
            .Single(row => row.Hold.BindingKey == Binding);
        Assert.Equal("newer", projected.Schedule!.ResetIntentId);
        Assert.Equal(future, projected.Schedule.NextProbeAt);
        // A successful check on the different binding cannot clear this hold.
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
        Assert.Equal(future, Row(fixture).Schedule!.NextProbeAt);
        fixture.Clock.UtcNow = future;
        fixture.Tick();
        Assert.Equal(2, fixture.Calls);
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
    }

    [Fact]
    public void SuccessTiedWithExhaustion_KeepsHoldAndRecoverySchedule()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        var success = fixture.NewGoal();
        var task = success.Tasks.Single();
        fixture.Kernel.RecordTaskDispatch(success.Id, task.Id,
            new("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory, fixture.Clock.UtcNow.AddSeconds(-1), ProviderName: "xAI"));
        fixture.Kernel.RecordTaskVerification(success.Id, task.Id,
            new("fixture", fixture.Workspace.ExecutionDirectory, 0, "Verified response", "", fixture.Clock.UtcNow,
                CompletionVerdictVerifiedSuccess: true));
        var row = Row(fixture);
        Assert.NotNull(row.Schedule);
        Assert.Equal(1, row.Schedule.Attempt);
        Assert.Equal(fixture.Due, row.Schedule.NextProbeAt);
        fixture.Clock.UtcNow = fixture.Due;
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsecutiveTaskOutcomes_PreserveBackoffAfterLaterExhaustion(bool reset)
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        fixture.Clock.UtcNow = fixture.Due;
        if (reset) Reset(fixture, "current", fixture.Due);
        var kinds = new[] { ProviderFailureKind.AuthenticationRequired, ProviderFailureKind.Unknown,
            ProviderFailureKind.BudgetExhausted, ProviderFailureKind.BudgetExhausted };
        for (var i = 0; i < kinds.Length; i++)
        {
            fixture.Clock.UtcNow = Row(fixture).Schedule!.NextProbeAt;
            var waiting = fixture.NewGoal();
            var task = waiting.Tasks.Single();
            fixture.WaitingTask = task.Id;
            ProviderBudgetProbeAdmission.Apply(fixture.Kernel, waiting, [task.Id], fixture.Preflight, fixture.Clock.UtcNow);
            var started = fixture.Clock.UtcNow;
            fixture.Kernel.RecordTaskDispatch(waiting.Id, task.Id,
                new("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory, started, ProviderName: "xAI"));
            fixture.Clock.UtcNow = started.AddMinutes(1);
            fixture.Kernel.RecordTaskVerification(waiting.Id, task.Id,
                new("fixture", fixture.Workspace.ExecutionDirectory, 1, "", "fixture", fixture.Clock.UtcNow,
                    ProviderFailureKind: kinds[i], DispatchStartedAt: started));
            var row = Row(fixture);
            Assert.Equal(i + 2, row.Schedule!.Attempt);
            Assert.Equal(reset ? "current" : null, row.Schedule.ResetIntentId);
            Assert.Equal(fixture.Clock.UtcNow.AddMinutes(Math.Min(30 * Math.Pow(2, i + 1), 360)), row.Schedule.NextProbeAt);
            Assert.False(row.Schedule.ProbeInFlight);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedCheck_UsesOtherGoalsBindingHistory(bool priorCheck)
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("failed");
        if (priorCheck)
        {
            fixture.Clock.UtcNow = fixture.Due;
            fixture.Tick();
        }
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        var latest = fixture.NewGoal();
        fixture.Exhaust(latest);
        Assert.Equal(GoalStatus.Failed, latest.Status);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Kernel.EvictTerminalGoalAggregates([latest.Id]));
        var row = Row(fixture);
        Assert.Equal(latest.Id, row.Hold.SourceGoalId);
        Assert.Equal(priorCheck ? 3 : 2, row.Schedule!.Attempt);
        fixture.Clock.UtcNow = row.Schedule.NextProbeAt;
        var calls = 0;
        var adapter = new ProviderHoldAvailabilityProbe(WorkerProfileCatalog.Default(), (_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(0, "AVAILABLE", "", false));
        });
        var recorder = new ProviderResetRetainedSourceRecorder(() => repository);
        var prober = new ProviderHoldStandaloneProbe(adapter, fixture.Writer, fixture.Preflight,
            recorder.RecordCheck, () => fixture.Clock.UtcNow);
        prober.ObserveTick(fixture.Kernel, fixture.Clock.UtcNow);
        Assert.Equal(1, calls);
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        var stored = await repository.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(GoalStatus.Failed, stored.GetGoal(latest.Id).Status);
        Assert.Empty(ProviderUsageHoldView.Read(stored.ProviderHoldScope, fixture.Clock.UtcNow));
        var check = Assert.Single(stored.GetGoal(latest.Id).Timeline.Where(evt =>
            ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _)));
        Assert.True(ProviderBudgetHoldSchedules.TryParseStandaloneResult(check, out var result));
        Assert.Equal(row.Schedule.Attempt, result.Attempt);
        Assert.Equal("available", result.Outcome);
    }

    [Fact]
    public async Task RetainedTransaction_RejectsResultWhenStoredGenerationChanged()
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("available");
        fixture.Clock.UtcNow = fixture.Due;
        var row = Row(fixture);
        var note = ProviderBudgetHoldSchedules.FormatStandaloneResult(new(Binding, row.Hold.PlacedAt,
            row.Schedule!.NextProbeAt, row.Schedule.Attempt, fixture.Clock.UtcNow, "available", "fixture"));
        Reset(fixture, "stored-reset", fixture.Due);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel, TestContext.Current.CancellationToken);
        var recorder = new ProviderResetRetainedSourceRecorder(() => repository);
        Assert.False(recorder.RecordCheck(fixture.Kernel, row.Hold, note, fixture.Clock.UtcNow));
        var stored = await repository.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Single(ProviderUsageHoldView.Read(stored.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.DoesNotContain(stored.ProviderHoldScope.SelectMany(goal => goal.Timeline),
            evt => ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _));
    }

    private static ProviderUsageHoldRow Row(ProviderHoldStandaloneProbeTests.Fixture fixture) =>
        ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow)
            .Single(row => row.Hold.BindingKey == Binding);

    private static Goal StartTaskProbe(ProviderHoldStandaloneProbeTests.Fixture fixture)
    {
        var goal = fixture.NewGoal();
        var task = goal.Tasks.Single();
        fixture.WaitingTask = task.Id;
        ProviderBudgetProbeAdmission.Apply(fixture.Kernel, goal, [task.Id], fixture.Preflight, fixture.Clock.UtcNow);
        Assert.Single(goal.Timeline.Where(evt => ProviderBudgetHoldSchedules.TryParseProbe(evt, out _)));
        fixture.Kernel.RecordTaskDispatch(goal.Id, task.Id,
            new("grok-cli", "fixture", fixture.Workspace.ExecutionDirectory, fixture.Clock.UtcNow,
                ProviderName: "xAI", ModelName: "grok-4.7"));
        return goal;
    }

    private static void CompleteTaskProbe(ProviderHoldStandaloneProbeTests.Fixture fixture, Goal goal,
        ProviderFailureKind kind, bool success = false)
    {
        var task = goal.Tasks.Single();
        fixture.Kernel.RecordTaskVerification(goal.Id, task.Id,
            new("fixture", fixture.Workspace.ExecutionDirectory, success ? 0 : 1, success ? "AVAILABLE" : "", "fixture",
                fixture.Clock.UtcNow, ProviderFailureKind: kind, DispatchStartedAt: task.LastDispatch!.DispatchedAt,
                CompletionVerdictVerifiedSuccess: success));
        if (!success)
            fixture.Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Fixture provider check failed.");
        fixture.WaitingTask = null;
    }

    private static void Reset(ProviderHoldStandaloneProbeTests.Fixture fixture, string id,
        DateTimeOffset due, DateTimeOffset? issued = null) =>
        fixture.Kernel.RecordTaskNote(fixture.Source.Id, fixture.Source.Tasks.Single().Id,
            ProviderBudgetHoldSchedules.FormatReset(new(Binding, due, id)),
            operatorIntentCreatedAt: issued ?? fixture.Clock.UtcNow);
}
