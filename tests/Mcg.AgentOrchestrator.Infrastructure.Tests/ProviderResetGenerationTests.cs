using Mcg.AgentOrchestrator.App.Orchestration;
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
        fixture.Kernel.RecordTaskVerification(waiting.Id, task.Id,
            new("fixture", fixture.Workspace.ExecutionDirectory, exitCode, "AVAILABLE", "", fixture.Clock.UtcNow,
                ProviderFailureKind: kind, DispatchStartedAt: fixture.Due));
        var row = Row(fixture);
        Assert.False(row.Schedule!.ProbeInFlight);
        Assert.Equal(1, row.Schedule.Attempt);
        Assert.Equal(fixture.Due, row.Schedule.NextProbeAt);
        fixture.WaitingTask = null;
        fixture.Tick();
        Assert.Equal(1, fixture.Calls);
        Assert.Empty(ProviderUsageHoldView.Read(fixture.Kernel.ProviderHoldScope, fixture.Clock.UtcNow));
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
                DispatchStartedAt: fixture.Due));
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

    private static void Reset(ProviderHoldStandaloneProbeTests.Fixture fixture, string id,
        DateTimeOffset due, DateTimeOffset? issued = null) =>
        fixture.Kernel.RecordTaskNote(fixture.Source.Id, fixture.Source.Tasks.Single().Id,
            ProviderBudgetHoldSchedules.FormatReset(new(Binding, due, id)),
            operatorIntentCreatedAt: issued ?? fixture.Clock.UtcNow);
}
