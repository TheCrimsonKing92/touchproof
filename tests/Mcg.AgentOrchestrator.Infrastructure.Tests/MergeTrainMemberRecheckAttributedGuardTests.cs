using Mcg.AgentOrchestrator.Core;

// Parallel-safe: isolated TRX files and in-memory effects only.
public sealed class MergeTrainMemberRecheckAttributedGuardTests
{
    [Theory]
    [InlineData("ownership")]
    [InlineData("impact")]
    [InlineData("stack-frame")]
    [InlineData("reach")]
    [InlineData("path")]
    [InlineData("bisection")]
    public void AttributedRed_AnySource_NeverRunsMemberSweep(string source)
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        fixture.Receipt = fixture.Receipt with { GateAttemptId = $"fixture-{source}" };
        var outcome = fixture.Run((_, _, _, _) => null, attributed: fixture.Members[0]);
        Assert.Null(outcome);
        AssertUnused(fixture);
    }

    [Theory]
    [InlineData("message-subject")]
    [InlineData("second-attempt")]
    [InlineData("two-members")]
    [InlineData("infrastructure")]
    [InlineData("passed")]
    public void IneligibleTrain_NeverRunsOrRecordsMemberSweep(string mode)
    {
        using var fixture = new MergeTrainMemberRecheckFixture(mode == "two-members" ? 2 : 3);
        if (mode is "infrastructure" or "passed") fixture.Receipt = fixture.Receipt with
            { Outcome = mode == "passed" ? MergeTrainGateOutcome.Passed : MergeTrainGateOutcome.InfrastructureFailure };
        var outcome = fixture.Run((_, _, _, _) => null,
            messageSubject: mode == "message-subject", attempt: mode == "second-attempt" ? 1 : 0);
        Assert.Null(outcome);
        AssertUnused(fixture);
    }

    private static void AssertUnused(MergeTrainMemberRecheckFixture fixture)
    {
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Implicated);
        Assert.Empty(fixture.Ejections);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Cache.Count);
    }
}
