using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: kernel snapshots only, no process or repository state.
public sealed class OwnerConsoleDeliveredForReviewTextTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private const string Url = "https://github.com/octo/sample/pull/7";

    [Fact]
    public void Board_and_detail_show_recorded_pr_number_while_held()
    {
        var goal = CreateGoal();
        Assert.Equal("delivered for review: PR 7", OwnerConsoleDeliveredForReviewText.For(goal));
        Assert.Equal("delivered for review: PR 7", OwnerConsoleGoalLanding.Line(goal, null, TimeZoneInfo.Utc));
        Assert.Equal("delivered for review: PR 7", OwnerConsoleStageDescriber.Describe(goal,
            OwnerConsoleBoardSortKey.For(goal), null, At));
        Assert.Equal("delivered for review: " + Url, goal.CurrentHold!.Blocker);
    }

    [Theory]
    [InlineData(GoalStatus.Completed, false)]
    [InlineData(GoalStatus.Verified, true)]
    public void Terminal_or_escalated_delivery_keeps_existing_presentation(GoalStatus status, bool closed)
    {
        Assert.Null(OwnerConsoleDeliveredForReviewText.For(CreateGoal(status, closed)));
    }

    private static Goal CreateGoal(GoalStatus status = GoalStatus.Verified, bool closed = false)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111", "Pull request", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
        {
            Status = status,
            DeliveredForReview = new(7, Url, new string('a', 40), "attempt", At),
            CurrentHold = new("hold", "Verified", "delivered for review: " + Url, At),
            Timeline = closed ? [new(goal.Id, null, ProgressKind.GoalPolicyDecision,
                "pull request closed unmerged: " + Url, At.AddSeconds(1))] : []
        }).ToArray() });
        return harness.Kernel.Goals.Single();
    }
}
