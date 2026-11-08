using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory kernel and brief rendering only.
public sealed class GoalRepositoryBriefSectionTests
{
    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void Render_EvidenceRole_ListsKeysAndRequiresRepository(AgentRole role)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Verify repository evidence", role);
        var goal = kernel.CreateGoal("Repository evidence", [task]);
        var worktrees = new Dictionary<string, string> { ["default"] = "home-goal", ["crosswalk"] = "crosswalk-goal" };
        var brief = kernel.BuildTaskBriefSource(goal.Id, task.Id, repositoryWorktrees: worktrees);
        var text = brief.ProjectLegacyMarkedTextV1(false).Content;
        Assert.Contains("- crosswalk: crosswalk-goal", text, StringComparison.Ordinal);
        Assert.Contains("- default: home-goal", text, StringComparison.Ordinal);
        Assert.Contains("Evidence-request declared repository keys: crosswalk, default.", text, StringComparison.Ordinal);
        Assert.Contains("non-default repository must carry the `repository` field with its declared key", text, StringComparison.Ordinal);
        Assert.Contains("test_project and test_class use that repository's own accepted forms", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_UnkeyedGoalOrDeveloper_PreservesPriorGuidance()
    {
        Assert.Empty(GoalRepositoryBriefSection.Render(null, AgentRole.Tester));
        var worktrees = new Dictionary<string, string> { ["default"] = "home" };
        Assert.Equal(new[] { "\r\n## Repositories", "- default: home" }, GoalRepositoryBriefSection.Render(worktrees, AgentRole.Developer));
    }
}
