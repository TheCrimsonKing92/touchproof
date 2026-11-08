using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its repositories and stores; deferrals are injected at the check seam.
public sealed class MultiRepositoryAcceptanceDeferralTests
{
    [Theory]
    [InlineData("default", "slots")]
    [InlineData("crosswalk", "slots")]
    [InlineData("default", "lock")]
    [InlineData("crosswalk", "lock")]
    [InlineData("default", "infrastructure")]
    [InlineData("crosswalk", "infrastructure")]
    [InlineData("default", "cancelled")]
    [InlineData("crosswalk", "cancelled")]
    public void AdvanceOnce_RepositoryDefers_HoldsWithoutReceiptFailureOrRetry(string key, string kind)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var exception = Deferral(kind);
        fixture.OnCheck = repository => { if (repository == key) throw exception; };

        var result = fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);

        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("multi-repository-acceptance-pending", hold.Reason);
        Assert.Contains(key + ": " + exception.GetType().Name, hold.Reason);
        Assert.Null(hold.Decision);
        AssertDeferred(fixture, key);

        // Once the transient condition clears, retry the whole tuple, including any earlier pass.
        fixture.OnCheck = null;
        fixture.Calls.Clear();
        var retry = fixture.Driver().AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(retry.Outcome);
        Assert.True(fixture.Gate.Store.TryRead(fixture.Goal.Id)!.Passed);
        Assert.Equal(new[] { "default", "crosswalk" }, fixture.Calls.Select(call => call.Key));
        Assert.Empty(fixture.RetryMessages);
    }

    [Theory]
    [InlineData("acceptance", "default")]
    [InlineData("acceptance", "crosswalk")]
    [InlineData("accept", "default")]
    [InlineData("accept", "crosswalk")]
    public void Execute_RepositoryDefers_HoldsWithoutReceiptFailureOrRetry(string command, string key)
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var exception = Deferral(key == "default" ? "slots" : "lock");
        fixture.OnCheck = repository => { if (repository == key) throw exception; };

        CliCommandHandlers.Execute(
            [command, fixture.Goal.Id.Value, "--autonomy", AutonomyPolicy.SupervisedAuto.Name], fixture.Context());

        AssertDeferred(fixture, key);
        Assert.Contains("multi-repository-acceptance-pending", fixture.Goal.LandingBlocker!.Detail);
        Assert.DoesNotContain(MultiRepositoryGoal.Route, fixture.Goal.LandingBlocker.Kind);
    }

    private static void AssertDeferred(MultiRepositoryAcceptanceGateFixture fixture, string key)
    {
        Assert.Equal(key == "default" ? new[] { "default" } : new[] { "default", "crosswalk" },
            fixture.Calls.Select(call => call.Key));
        Assert.Null(fixture.Gate.Store.TryRead(fixture.Goal.Id));
        Assert.Null(fixture.Goal.RetainedAcceptanceFailure);
        Assert.Equal(WorkTaskStatus.Completed, Assert.Single(fixture.Goal.Tasks).Status);
        Assert.Empty(fixture.RetryMessages);
        Assert.Equal(0, fixture.LandingCalls);
    }

    private static Exception Deferral(string kind) => kind switch
    {
        "slots" => new DotnetBuildSlotsBusyException(new("fixture-stable-slot", [])),
        "lock" => new BuildLockBlockedException(new BuildLockAttribution("fixture.dll", [], "fixture")),
        "infrastructure" => new AcceptanceInfrastructureDeferredException("fixture-apparatus", null, "not executed"),
        "cancelled" => new AcceptanceAttemptCancelledException(
            AcceptanceAttemptCancellationDecision.Cancel(AcceptanceAttemptCancellationCause.AttemptInvalidated)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
