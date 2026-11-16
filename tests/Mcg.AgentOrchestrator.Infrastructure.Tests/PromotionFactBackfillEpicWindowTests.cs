using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: this test owns its Git repository and all state and portfolio stores.
public sealed class PromotionFactBackfillEpicWindowTests
{
    [Fact]
    public async Task Backfill_FourRecentAndOneOldLanding_UpdatesEpicWindowFromZeroToFour()
    {
        using var f = new PromotionFactBackfillFixture();
        var portfolio = new PortfolioStore(f.Workspace.PortfolioStorePath);
        var epic = await portfolio.AddEpicAsync("Historical deliveries");
        var goals = Enumerable.Range(0, 5).Select(_ => f.AddGoal()).ToArray();
        await f.Save();
        for (var i = 0; i < goals.Length; i++)
        {
            await portfolio.AssignGoalToEpicAsync(goals[i].Id.Value, epic.Id);
            f.Land(goals[i], at: i < 4 ? new(2026, 10, 15, 13 + i, 0, 0, TimeSpan.Zero)
                : new(2026, 10, 10, 14, 0, 0, TimeSpan.Zero));
        }
        var since = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        var before = Assert.Single(EpicProgressReadModel.Load(f.Workspace, since));
        Assert.Equal(epic.Id, before.Epic.Id);
        Assert.Equal(5, before.MemberGoals.Count);
        Assert.Equal(0, before.WindowLandedCount);
        var applied = f.Run(apply: true);
        using var json = applied.Json;
        Assert.Equal(0, applied.Exit);
        Assert.Equal(5, json.RootElement.GetProperty("written").GetInt32());
        var after = Assert.Single(EpicProgressReadModel.Load(f.Workspace, since));
        Assert.Equal(4, after.WindowLandedCount);
        Assert.Equal(5, after.MemberGoals.Count);
        Assert.All(after.MemberGoals, goal => Assert.NotNull(goal.PromotionFact));
    }
}
