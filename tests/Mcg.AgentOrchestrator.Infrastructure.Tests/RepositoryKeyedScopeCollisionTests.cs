using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: comparison is in memory; checkout paths are never read or written.
public sealed class RepositoryKeyedScopeCollisionTests
{
    [Xunit.Fact]
    public void Build_SameKeySamePath_ReportsOverlap()
    {
        var (goal, repositories) = Candidate("crosswalk");

        var report = GoalScopeCollisionAdvisor.Build([Brief("crosswalk")], [goal], repositories: repositories);

        var collision = Xunit.Assert.Single(report.Collisions);
        Xunit.Assert.Equal(ScopeCollisionKind.ExactFile, collision.Kind);
        Xunit.Assert.Equal(ScopeCollisionVerdict.OverlapDetected, report.Verdict);
        Xunit.Assert.Equal(goal.Id.Value, collision.GoalId);
        Xunit.Assert.Equal("schema/v1/README.md", collision.ProposedPath);
    }

    [Xunit.Fact]
    public void Build_DifferentKeysSamePath_ReportsNoOverlap()
    {
        var (goal, repositories) = Candidate("default");

        var report = GoalScopeCollisionAdvisor.Build([Brief("crosswalk")], [goal], repositories: repositories);

        Xunit.Assert.Empty(report.Collisions);
        Xunit.Assert.Equal(ScopeCollisionVerdict.NoOverlapDetected, report.Verdict);
        Xunit.Assert.Equal(1, report.ComparedGoalCount);
        Xunit.Assert.Empty(report.EvidenceGaps);
    }

    [Xunit.Fact]
    public void Build_AncestorInAnotherRepository_PreservesScopeForCollision()
    {
        var (goal, repositories) = Candidate("default");
        var proposal = "## Scope\nIn scope, home (key default):\n- `schema/`\n" +
            "In scope, other (key crosswalk):\n- `schema/v1/README.md`";

        var report = GoalScopeCollisionAdvisor.Build([proposal], [goal], repositories: repositories);

        Xunit.Assert.Equal(2, report.ProposedScopes.Count);
        Xunit.Assert.Equal(ScopeCollisionKind.DirectoryPrefix, Xunit.Assert.Single(report.Collisions).Kind);
    }

    private static string Brief(string key) =>
        $"## Scope\nIn scope, repository (key {key}):\n- `schema/v1/README.md`";

    private static (Goal Goal, GoalRepositoryDeclaration Repositories) Candidate(string key)
    {
        var root = Path.Combine(Path.GetTempPath(), "scope-collision", Guid.NewGuid().ToString("N"));
        var repositories = new GoalRepositoryDeclaration(["default", "crosswalk"],
            new Dictionary<string, string>
            {
                ["default"] = Path.Combine(root, "default"),
                ["crosswalk"] = Path.Combine(root, "crosswalk")
            });
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(Brief(key),
            [new TaskSpec(TaskId.New(), "Implement planned work.", AgentRole.Developer)]);
        kernel.DeclareGoalRepositories(goal.Id, repositories);
        return (goal, repositories);
    }
}
