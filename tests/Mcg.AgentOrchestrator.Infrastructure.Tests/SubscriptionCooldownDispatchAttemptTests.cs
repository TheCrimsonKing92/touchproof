using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = SubscriptionCooldownSubstitutionTests.Scenario;

// Parallel-safe: each scenario owns its temporary repository and injected clock; no live provider is used.
public sealed class SubscriptionCooldownDispatchAttemptTests
{
    private static readonly DateTimeOffset DispatchedAt = new(2030, 11, 14, 8, 42, 0, 566, TimeSpan.Zero);
    private static readonly DateTimeOffset StartedAt = DispatchedAt.AddMilliseconds(2692);
    private static readonly DateTimeOffset CompletedAt = new(2030, 11, 14, 8, 44, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EvaluatedAt = new(2030, 11, 14, 8, 45, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ResetAt = new(2030, 11, 18, 17, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void TaskCooldown_AttemptEqualsDispatchStartLater_ReturnsModelCooldown()
    {
        using var scenario = new Scenario("claude-fable-5-2");
        var task = RecordLimit(scenario, DispatchedAt);

        Assert.True(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, EvaluatedAt, out var cooldown));
        Assert.Equal("Anthropic", cooldown.ProviderName);
        Assert.Equal("fable", cooldown.ModelName);
        Assert.Equal(ResetAt, cooldown.RetryAfter);
        Assert.Equal(task.SubscriptionRetryAfter, cooldown.RetryAfter);
    }

    [Xunit.Fact]
    public void TaskCooldown_AttemptAndStartMatchNoDispatch_ReturnsNone()
    {
        using var scenario = new Scenario("claude-fable-5-2");
        var attemptAt = DispatchedAt.AddSeconds(1);
        var task = RecordLimit(scenario, attemptAt);

        Assert.DoesNotContain(task.DispatchHistory,
            dispatch => dispatch.DispatchedAt == attemptAt || dispatch.DispatchedAt == StartedAt);
        Assert.False(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, EvaluatedAt, out _));
    }

    [Xunit.Fact]
    public void FindDispatch_AttemptAndStartMatchDifferentRecords_SelectsAttempt()
    {
        using var scenario = new Scenario("claude-fable-5-2");
        var task = RecordLimit(scenario, DispatchedAt);
        var attemptDispatch = Assert.Single(task.DispatchHistory);
        var startedDispatch = attemptDispatch with { DispatchedAt = StartedAt };
        task.RecordDispatch(startedDispatch);
        Assert.Equal(2, task.DispatchHistory.Count);
        Assert.Equal(StartedAt, task.LastDispatch!.DispatchedAt);

        Assert.Same(attemptDispatch, SubscriptionLimitEvidence.FindDispatchForVerification(
            task, Assert.Single(task.VerificationHistory)));
    }

    [Xunit.Fact]
    public void TaskCooldown_NoAttemptStartEqualsDispatch_ReturnsCooldown()
    {
        using var scenario = new Scenario("claude-fable-5-2");
        var task = RecordLimit(scenario, null, DispatchedAt);

        Assert.True(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, EvaluatedAt, out var cooldown));
        Assert.Equal("Anthropic", cooldown.ProviderName);
        Assert.Equal("fable", cooldown.ModelName);
        Assert.Equal(ResetAt, cooldown.RetryAfter);
    }

    [Xunit.Fact]
    public void TaskCooldown_NoAttemptStartLaterThanDispatch_ReturnsNone()
    {
        using var scenario = new Scenario("claude-fable-5-2");
        var task = RecordLimit(scenario, null);

        Assert.False(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, EvaluatedAt, out _));
    }

    [Xunit.Fact]
    public void TaskCooldown_AttemptMismatchStartMatches_ReturnsNone()
    {
        using var scenario = new Scenario("claude-fable-5-2");
        var task = RecordLimit(scenario, DispatchedAt.AddSeconds(1), DispatchedAt);

        Assert.False(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, EvaluatedAt, out _));
    }

    [Xunit.Fact]
    public void ApplyBatch_PlannerModelLimit_MovesToNextAvailableSeatOutsideLimit()
    {
        using var scenario = new Scenario("claude-fable-5-2");
        var offline = Scenario.Agent("offline-opus", "Anthropic", "claude-cli", "claude-opus-5-5")
            with { Status = AgentStatus.Offline };
        var limited = Scenario.Agent("other-fable", "Anthropic", "claude-cli", "claude-fable-5-2");
        var eligible = Scenario.Agent("available-opus", "Anthropic", "claude-cli", "claude-opus-5-5");
        scenario.Agents = [scenario.Primary, offline, limited, eligible, scenario.Alternate];
        scenario.Clock.UtcNow = DispatchedAt;
        var goal = scenario.NewGoal();
        var task = RecordLimit(scenario, DispatchedAt, goal: goal);
        Assert.Equal(AgentStatus.Available, limited.Status);
        Assert.Equal(AgentStatus.Available, eligible.Status);
        Assert.All(scenario.Agents, agent => Assert.Equal(AgentRole.Planner, agent.Role));

        scenario.Resolver().ApplyBatch(goal, [task.Id]);

        var snapshot = scenario.Kernel.ExportGoalSnapshot(goal.Id);
        Assert.Equal(eligible.Id.Value, Assert.Single(snapshot.Tasks).AssignedAgentId);
        Assert.Null(scenario.Kernel.GetTask(goal.Id, task.Id).SubscriptionRetryAfter);
        Assert.DoesNotContain(scenario.Preflights, probe => probe.AgentId == offline.Id);
        Assert.Contains(scenario.Preflights, probe => probe.AgentId == limited.Id && !probe.Allowed);
        Assert.Contains(scenario.Preflights, probe => probe.AgentId == eligible.Id && probe.Allowed);
        Assert.DoesNotContain(scenario.Preflights, probe => probe.AgentId == scenario.Alternate.Id);
        var substitution = Assert.Single(snapshot.Timeline,
            entry => entry.TaskId == task.Id.Value && entry.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.Contains($"held agent={scenario.Primary.Id.Value}", substitution.Message, StringComparison.Ordinal);
        Assert.Contains($"substitute={eligible.Id.Value}", substitution.Message, StringComparison.Ordinal);
        Assert.Contains($"reset={ResetAt:O}", substitution.Message, StringComparison.Ordinal);
        Assert.Contains(Scenario.FableLimit, substitution.Message, StringComparison.Ordinal);
        Assert.Contains(snapshot.Timeline, entry => entry.TaskId == task.Id.Value &&
            entry.Message.Contains("passed over", StringComparison.Ordinal) &&
            entry.Message.Contains(limited.Id.Value, StringComparison.Ordinal) &&
            entry.Message.Contains("provider Anthropic is cooling down", StringComparison.Ordinal));
    }

    private static TaskSpec RecordLimit(Scenario scenario, DateTimeOffset? attemptAt,
        DateTimeOffset? startedAt = null, Goal? goal = null)
    {
        startedAt ??= StartedAt;
        scenario.Clock.UtcNow = DispatchedAt;
        goal ??= scenario.NewGoal();
        var task = Assert.Single(goal.Tasks);
        Assert.Equal(AgentRole.Planner, task.RequiredRole);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);

        var dispatch = new TaskDispatchRecord("claude-cli", "fixture worker",
            scenario.Workspace.ResolveExecutionDirectory(goal.Id), DispatchedAt,
            ProviderName: "Anthropic", GoalId: goal.Id, ModelName: "claude-fable-5-2",
            AssignedAgentId: scenario.Primary.Id.Value,
            WorkerProviderKind: WorkerProviderCatalog.Default().ResolveProfile("claude-cli").Identity.Kind);
        scenario.Kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        scenario.Clock.UtcNow = CompletedAt;
        scenario.Kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(dispatch.Command, dispatch.WorkingDirectory, 1,
                Scenario.FableLimit, "", CompletedAt,
                DispatchStartedAt: startedAt, DispatchAttemptAt: attemptAt));
        scenario.Clock.UtcNow = EvaluatedAt;

        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(ResetAt, task.SubscriptionRetryAfter);
        Assert.True(ProviderLimitSignals.IsSubscriptionRetryDeferred(task, EvaluatedAt, out var retryAfter));
        Assert.Equal(ResetAt, retryAfter);
        Assert.Equal(DispatchedAt, Assert.Single(task.DispatchHistory).DispatchedAt);
        var verification = Assert.Single(task.VerificationHistory);
        Assert.False(verification.Succeeded);
        Assert.Equal(attemptAt, verification.DispatchAttemptAt);
        Assert.Equal(startedAt, verification.DispatchStartedAt);
        Assert.True(SubscriptionLimitEvidence.IsRecoverableSubscriptionLimitFailure(verification));
        return task;
    }
}
