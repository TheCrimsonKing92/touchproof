using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its SQLite backlog store.
public sealed class SliceLandingRemainingScopeTests
{
    private const string Commit = "1234567890abcdef";

    [Theory]
    [InlineData("## Scope\nFollow-up slices: alpha; beta", "Follow-up slices: alpha; beta")]
    [InlineData("## Scope\r\n  Follow-up slices:  alpha; beta\t\r\nFollow-up slices: gamma  \r\nOther text",
        "  Follow-up slices:  alpha; beta\t\nFollow-up slices: gamma  ")]
    [InlineData("## Scope\nOne slice only", "Remaining slices: not recorded")]
    [InlineData("## Scope\nFollow-up slices: \t\nUnrelated text", "Remaining slices: not recorded")]
    public async Task Slice_landing_persists_the_declaration_or_fallback_once(string brief, string expected)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var item = await fixture.Store.AddAsync("Slice landing scope");
        var goal = CreateSlice(item.Id, brief);

        RecordLanding(fixture, goal);
        RecordLanding(fixture, goal);

        var stored = Assert.IsType<BacklogItem>(await fixture.Store.GetByExactIdAsync(item.Id));
        Assert.Equal(BacklogItemStatus.Open, stored.Status);
        Assert.Equal($"{Header(goal)}\n{expected}", Assert.Single(stored.Notes).Text);
    }

    [Fact]
    public async Task Replaying_a_historical_slice_header_does_not_append_a_second_landing()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var item = await fixture.Store.AddAsync("Historical slice landing");
        var goal = CreateSlice(item.Id, "## Scope\nFollow-up slices: alpha; beta");
        var header = Header(goal);
        var initial = await fixture.Store.TryRecordOpenItemLandingAsync(item.Id, header);
        Assert.Equal(BacklogLandingDisposition.Recorded, initial.Disposition);

        RecordLanding(fixture, goal);

        var stored = Assert.IsType<BacklogItem>(await fixture.Store.GetByExactIdAsync(item.Id));
        Assert.Equal(header, Assert.Single(stored.Notes).Text);
    }

    private static Goal CreateSlice(string itemId, string brief)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(brief);
        kernel.SetGoalSourceBacklogItemLink(goal.Id, itemId, SourceBacklogCoverage.Slice);
        return goal;
    }

    private static string Header(Goal goal) => $"Recorded slice landing for goal {goal.Id.Value}. integrateCommit={Commit}";

    private static void RecordLanding(CliAuthorDraftCommandTests.Fixture fixture, Goal goal) =>
        Assert.False(GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, fixture.Workspace.BacklogStorePath,
            integrateCommitSha: Commit));
}
