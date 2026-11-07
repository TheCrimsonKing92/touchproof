using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: each case owns its repositories, registry and state stores.
public sealed class CliMultiRepositoryAcceptancePolicyTests
{
    [Theory]
    [InlineData("acceptance", "observe")]
    [InlineData("acceptance", "safe-auto")]
    [InlineData("accept", "observe")]
    [InlineData("accept", "safe-auto")]
    public void Execute_DisallowedAcceptance_RecordsPolicyWithoutChecksReceiptOrRetry(string command, string policy)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture { FailSecondary = true };
        var task = Assert.Single(fixture.Goal.Tasks);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.True(AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(fixture.Goal));
        var expectedDecision = AutonomyPolicy.Parse(policy).DecisionMessage(AutonomyAction.Acceptance,
            command == "acceptance" ? "acceptance merge" : "accept", allowed: false);

        Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
            [command, fixture.Goal.Id.Value, "--autonomy", policy], fixture.Context()));

        Assert.Contains(fixture.Goal.Timeline, item => item.Message == expectedDecision);
        Assert.Empty(fixture.Calls);
        Assert.Null(fixture.Gate.Store.TryRead(fixture.Goal.Id));
        Assert.Null(fixture.Goal.RetainedAcceptanceFailure);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Empty(fixture.RetryMessages);
        Assert.Null(fixture.Goal.LandingBlocker);
    }
}
