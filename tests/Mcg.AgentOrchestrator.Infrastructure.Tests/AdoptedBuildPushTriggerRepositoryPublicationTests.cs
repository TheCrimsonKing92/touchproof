using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: private delegates and sinks, no machine-wide settings or processes.
public sealed class AdoptedBuildPushTriggerRepositoryPublicationTests
{
    [Theory]
    [InlineData(false, "", "", 0)]
    [InlineData(true, "pending", "", 0)]
    [InlineData(true, "", "failed", 0)]
    [InlineData(true, "pending", "failed", 0)]
    [InlineData(true, "", "", 1)]
    public void AdoptionGuards_ApplyToHomeAndRepositoryPublication(
        bool enabled, string pending, string failed, int expected)
    {
        var calls = new List<string>();
        var events = new List<string>();
        var attention = new List<string>();
        var trigger = new AdoptedBuildPushTrigger(
            () => ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = enabled },
            commit => calls.Add("home:" + commit), events.Add,
            attention.Add, commit => calls.Add("repositories:" + commit));

        trigger.OnFinalized("adopted", pending, failed);

        Assert.Equal(expected == 0 ? Array.Empty<string>() : new[] { "home:adopted", "repositories:adopted" }, calls);
        Assert.Empty(events);
        Assert.Empty(attention);
    }

    [Fact]
    public void UnreadablePolicy_DoesNotInvokeRepositoryPublication()
    {
        var events = new List<string>();
        var attention = new List<string>();
        var publications = 0;
        var trigger = new AdoptedBuildPushTrigger(() => throw new FormatException(),
            _ => Assert.Fail("Home push cannot run"), events.Add, attention.Add, _ => publications++);

        trigger.OnFinalized("adopted", "", "");

        Assert.Equal(0, publications);
        Assert.Contains("reason=policy-unreadable", Assert.Single(events));
        Assert.Single(attention);
    }

    [Fact]
    public void HomeServiceThrows_StillInvokesRepositoryPublication()
    {
        var events = new List<string>();
        var attention = new List<string>();
        var calls = new List<string>();
        var trigger = new AdoptedBuildPushTrigger(
            () => ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = true },
            _ => throw new IOException(), events.Add, attention.Add, calls.Add);

        trigger.OnFinalized("adopted", "", "");

        Assert.Equal("adopted", Assert.Single(calls));
        Assert.Equal("ADOPTED_BUILD_PUSH_HELD commit=adopted reason=service-failed", Assert.Single(events));
        Assert.Single(attention);
    }

    [Fact]
    public void RepositoryInfrastructureFails_ReportsFailureWithoutPrivateDiagnostic()
    {
        var events = new List<string>();
        var attention = new List<string>();
        var homeCalls = 0;
        var trigger = new AdoptedBuildPushTrigger(
            () => ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = true },
            _ => homeCalls++, events.Add, attention.Add, _ => throw new IOException("private diagnostic"));

        trigger.OnFinalized("adopted", "", "");

        Assert.Equal(1, homeCalls);
        Assert.Equal("ADOPTED_BUILD_PUSH_HELD commit=adopted reason=repository-publication-failed", Assert.Single(events));
        var body = Assert.Single(attention);
        Assert.Contains("manually publish", body);
        Assert.DoesNotContain("push-adopted", body);
        Assert.DoesNotContain("private diagnostic", body);
    }

    [Fact]
    public void RepositoryPublicationCanceled_PropagatesCancellation()
    {
        var trigger = new AdoptedBuildPushTrigger(
            () => ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = true },
            _ => { }, _ => Assert.Fail("Cancellation is not a hold"), _ => Assert.Fail("Cancellation is not attention"),
            _ => throw new OperationCanceledException());
        Assert.Throws<OperationCanceledException>(() => trigger.OnFinalized("adopted", "", ""));
    }
}
