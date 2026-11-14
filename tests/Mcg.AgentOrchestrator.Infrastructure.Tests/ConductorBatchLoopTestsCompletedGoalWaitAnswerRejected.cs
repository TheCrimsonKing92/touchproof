using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ConductorBatchLoopTestsCompletedGoalWaitAnswerApplied.Fixture;

// Each case owns its SQLite stores; no processes or shared state are exercised.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsCompletedGoalWaitAnswerRejected(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const int CompletedWaitRejectionHangFailsafeMilliseconds = 30_000;

    [Fact(Timeout = CompletedWaitRejectionHangFailsafeMilliseconds)]
    public async Task Answer_NonDeferredWait_KeepsTerminalRejection()
    {
        using var fixture = await Fixture.Create(availability: "post-implementation");
        var intent = await fixture.Store.EnqueueAsync(fixture.Answer(fixture.Waits[0].Id.Value));

        fixture.Run(MakeDriver());

        await AssertTerminalRejection(fixture, intent);
        var durable = await fixture.Reload();
        Assert.False(Assert.Single(durable.HumanInputRequests).IsCompleted);
    }

    [Fact(Timeout = CompletedWaitRejectionHangFailsafeMilliseconds)]
    public async Task NonAnswer_WithDeferredWait_KeepsTerminalRejection()
    {
        using var fixture = await Fixture.Create();
        var intent = await fixture.Store.EnqueueAsync(fixture.Intent(OperatorIntentVerbs.GoalNote,
            new GoalNoteOperatorIntentPayload("An operator note after completion")));

        fixture.Run(MakeDriver());

        await AssertTerminalRejection(fixture, intent);
        Assert.False(Assert.Single((await fixture.Reload()).HumanInputRequests).IsCompleted);
    }

    [Theory(Timeout = CompletedWaitRejectionHangFailsafeMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Answer_ClosedWaitOrNoOpenBareTarget_KeepsTerminalRejection(bool bare)
    {
        using var fixture = await Fixture.Create(closeWait: true);
        var intent = await fixture.Store.EnqueueAsync(fixture.Answer(bare ? "" : fixture.Waits[0].Id.Value));

        fixture.Run(MakeDriver());

        await AssertTerminalRejection(fixture, intent);
        var wait = Assert.Single((await fixture.Reload()).HumanInputRequests);
        Assert.True(wait.IsCompleted);
        Assert.Equal("Earlier observation", wait.Answer);
    }

    [Fact(Timeout = CompletedWaitRejectionHangFailsafeMilliseconds)]
    public async Task Answer_UnknownExplicitTarget_KeepsTerminalRejection()
    {
        using var fixture = await Fixture.Create();
        var intent = await fixture.Store.EnqueueAsync(fixture.Answer(Guid.NewGuid().ToString("N")));

        fixture.Run(MakeDriver());

        await AssertTerminalRejection(fixture, intent);
        Assert.False(Assert.Single((await fixture.Reload()).HumanInputRequests).IsCompleted);
    }

    [Fact(Timeout = CompletedWaitRejectionHangFailsafeMilliseconds)]
    public async Task BareAnswer_TwoOpenWaits_RejectsWithBothIdsAndDistinctReason()
    {
        using var fixture = await Fixture.Create(waitCount: 2);
        var intent = await fixture.Store.EnqueueAsync(fixture.Answer(""));

        fixture.Run(MakeDriver());

        var stored = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, stored.Status);
        Assert.Contains("reasonCode=completed-goal-deferred-wait-ambiguous", stored.Outcome!, StringComparison.Ordinal);
        Assert.DoesNotContain("goal-terminal-evicted", stored.Outcome!, StringComparison.Ordinal);
        Assert.All(fixture.Waits, wait => Assert.Contains(wait.Id.Value, stored.Outcome!, StringComparison.Ordinal));
        var durable = await fixture.Reload();
        Assert.Equal(2, durable.HumanInputRequests.Count);
        Assert.All(durable.HumanInputRequests, wait => Assert.False(wait.IsCompleted));
        Assert.Equal(GoalStatus.Completed, durable.GetGoal(fixture.Goal.Id).Status);
        Assert.DoesNotContain(durable.GetGoal(fixture.Goal.Id).Timeline,
            entry => entry.OperatorIntentApplied?.IntentId == intent.Id);
    }

    private static async Task AssertTerminalRejection(Fixture fixture, OperatorIntentRecord intent)
    {
        var stored = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, stored.Status);
        Assert.Contains("reasonCode=goal-terminal-evicted", stored.Outcome!, StringComparison.Ordinal);
        var goal = (await fixture.Reload()).GetGoal(fixture.Goal.Id);
        Assert.Equal(GoalStatus.Completed, goal.Status);
        Assert.DoesNotContain(goal.Timeline, entry => entry.OperatorIntentApplied?.IntentId == intent.Id);
    }
}
