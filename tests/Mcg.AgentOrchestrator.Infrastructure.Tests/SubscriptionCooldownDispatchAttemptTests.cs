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

    private static TaskSpec RecordLimit(Scenario scenario, DateTimeOffset attemptAt)
    {
        scenario.Clock.UtcNow = DispatchedAt;
        var goal = scenario.NewGoal();
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
                DispatchStartedAt: StartedAt, DispatchAttemptAt: attemptAt));
        scenario.Clock.UtcNow = EvaluatedAt;

        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(ResetAt, task.SubscriptionRetryAfter);
        Assert.True(ProviderLimitSignals.IsSubscriptionRetryDeferred(task, EvaluatedAt, out var retryAfter));
        Assert.Equal(ResetAt, retryAfter);
        Assert.Equal(DispatchedAt, Assert.Single(task.DispatchHistory).DispatchedAt);
        var verification = Assert.Single(task.VerificationHistory);
        Assert.False(verification.Succeeded);
        Assert.Equal(attemptAt, verification.DispatchAttemptAt);
        Assert.Equal(StartedAt, verification.DispatchStartedAt);
        Assert.True(SubscriptionLimitEvidence.IsRecoverableSubscriptionLimitFailure(verification));
        return task;
    }
}
