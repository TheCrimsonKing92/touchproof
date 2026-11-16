using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each test owns its Git repository, events and migrated SQLite store.
public sealed class PromotionFactBackfillPlannerTests
{
    [Fact]
    public async Task SingleMergeEvidenceEvent_ReachableMain_PlansUtcFactWithFirstParent()
    {
        using var f = new PromotionFactBackfillFixture();
        var goal = f.AddGoal();
        await f.Save();
        f.Land(goal);
        Assert.Equal(f.Prior, f.Git("rev-parse", f.Promoted + "^1"));
        Assert.Equal(f.Candidate, f.Git("rev-parse", f.Promoted + "^2"));
        var plan = f.Plan();
        var fact = Assert.Single(plan.PlannedGoals);
        Assert.Equal(goal.Id.Value, fact.GoalId);
        Assert.Equal(PromotionFactBackfillFixture.At.ToUniversalTime(), fact.PromotedAt);
        Assert.Equal(TimeSpan.Zero, fact.PromotedAt.Offset);
        Assert.Equal(f.Promoted, fact.PromotedMainRevision);
        Assert.Equal(f.Prior, fact.PriorMainRevision);
        Assert.Equal(f.Candidate, fact.CandidateRevision);
        Assert.Equal("reconstructed:merge-evidence", fact.Provenance);
        Assert.Empty(plan.UnknownGoals);
        Assert.Empty(plan.ConflictGoals);
    }

    [Fact]
    public async Task MissingEventMissingMainAndUnreachableMain_StayUnknownWithDistinctReasons()
    {
        using var f = new PromotionFactBackfillFixture();
        var noEvent = f.AddGoal();
        var noMain = f.AddGoal();
        var unreachable = f.AddGoal();
        await f.Save();
        f.Writer().AppendGoalLanded(noMain.Id, f.Workspace.IntegrationBranch, "goal/plain");
        f.Land(unreachable, f.Unreachable);
        var plan = f.Plan();
        Assert.Empty(plan.PlannedGoals);
        Assert.Empty(plan.ConflictGoals);
        Assert.Equal(3, plan.UnknownGoals.Count);
        Assert.Equal("no-landing-event", plan.UnknownGoals.Single(g => g.GoalId == noEvent.Id.Value).Reason);
        Assert.Equal("no-main-revision", plan.UnknownGoals.Single(g => g.GoalId == noMain.Id.Value).Reason);
        Assert.Equal("main-revision-not-reachable", plan.UnknownGoals.Single(g => g.GoalId == unreachable.Id.Value).Reason);
    }

    [Fact]
    public async Task ConflictingMainRevisions_ReportConflictAndApplyWritesNothing()
    {
        using var f = new PromotionFactBackfillFixture();
        var goal = f.AddGoal();
        await f.Save();
        f.Land(goal, f.Prior);
        f.Land(goal);
        var plan = f.Plan();
        Assert.Empty(plan.PlannedGoals);
        Assert.Empty(plan.UnknownGoals);
        var conflict = Assert.Single(plan.ConflictGoals);
        Assert.Equal(goal.Id.Value, conflict.GoalId);
        Assert.Equal(new[] { f.Prior, f.Promoted }.Order(), conflict.MainRevisions);
        var result = f.Run(apply: true);
        using var json = result.Json;
        Assert.Equal(0, result.Exit);
        Assert.Equal(1, json.RootElement.GetProperty("conflicts").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("written").GetInt32());
        Assert.Empty(f.Rows());
    }

    [Fact]
    public async Task PlainThenAncestry_UsesEarliestTimeAndSkipsMalformedLines()
    {
        using var f = new PromotionFactBackfillFixture();
        var goal = f.AddGoal();
        await f.Save();
        var earlier = PromotionFactBackfillFixture.At.AddDays(-2);
        f.Writer(earlier).AppendGoalLanded(goal.Id, f.Workspace.IntegrationBranch, "goal/plain");
        f.Writer().AppendGoalLandedFromAncestry(goal.Id, "goal/ancestry", f.Candidate, f.Promoted);
        File.AppendAllText(Path.Combine(f.Workspace.GoalLifecycleEventsDirectory, goal.Id.Value + ".jsonl"), "broken json\n[]\n");
        var plan = f.Plan();
        var fact = Assert.Single(plan.PlannedGoals);
        Assert.Equal(earlier.ToUniversalTime(), fact.PromotedAt);
        Assert.Equal("reconstructed:ancestry", fact.Provenance);
        Assert.Equal(f.Candidate, fact.CandidateRevision);
        Assert.Equal(2, plan.MalformedLines);
    }

    [Theory]
    [InlineData("integrateSha", "", "no-candidate-revision")]
    [InlineData("integrateSha", "abc", "revision-not-full-hex")]
    [InlineData("mainSha", "abc", "revision-not-full-hex")]
    [InlineData("source", "", "invalid-source")]
    [InlineData("source", "Not-a-slug", "invalid-source")]
    public async Task IncompleteEvidence_StaysUnknown(string field, string value, string reason)
    {
        using var f = new PromotionFactBackfillFixture();
        var goal = f.AddGoal();
        await f.Save();
        f.Land(goal);
        var path = Path.Combine(f.Workspace.GoalLifecycleEventsDirectory, goal.Id.Value + ".jsonl");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node[field] = value;
        File.WriteAllText(path, node.ToJsonString() + "\n");
        var plan = f.Plan();
        Assert.Empty(plan.PlannedGoals);
        Assert.Equal(reason, Assert.Single(plan.UnknownGoals).Reason);
    }

    [Fact]
    public async Task RootCommitWithoutParent_StaysUnknown()
    {
        using var f = new PromotionFactBackfillFixture();
        var goal = f.AddGoal();
        await f.Save();
        f.Land(goal, f.Git("rev-list", "--max-parents=0", f.Workspace.IntegrationBranch));
        var plan = f.Plan();
        Assert.Empty(plan.PlannedGoals);
        Assert.Equal("main-revision-has-no-parent", Assert.Single(plan.UnknownGoals).Reason);
    }
}
