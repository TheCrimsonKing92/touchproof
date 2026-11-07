using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.Core;
using System.Text.Json;
using Xunit;

// Parallel-safe: case-owned repositories, worktrees and kernel; no process-global changes.
public sealed class GoalRepositoriesCandidateIdentityTests
{
    [Fact]
    public void Configure_SecondaryCommit_ChangesGoalCandidate()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        SeedAcceptanceManifests(fixture);
        var goal = fixture.CreateGoal();
        var home = fixture.CreateHomeWorktree(goal);
        Assert.True(GoalRepositoryDispatchProvisioning.TryProvision(goal, goal.Tasks.Single(), out var failure), failure);
        var resolve = GoalWorktreeCandidateContext.Configure(fixture.Kernel, fixture.Workspace);
        var before = resolve(goal);
        Assert.True(string.IsNullOrEmpty(before.Failure), before.Failure);
        Assert.NotNull(before.Identity);
        var head = GoalRepositoryWorktreeProvisionerFixture.Run(home, "rev-parse", "HEAD");

        GoalRepositoryWorktreeProvisionerFixture.CommitChange(fixture.Crosswalk.WorktreePath(goal.Id, "crosswalk"));

        var after = resolve(goal);
        Assert.True(string.IsNullOrEmpty(after.Failure), after.Failure);
        Assert.NotNull(after.Identity);
        Assert.Equal(head, GoalRepositoryWorktreeProvisionerFixture.Run(home, "rev-parse", "HEAD"));
        Assert.NotEqual(before.Identity!.Canonical, after.Identity!.Canonical);
        var restored = JsonSerializer.Deserialize<CandidateIdentity>(JsonSerializer.Serialize(after.Identity));
        Assert.Equal(after.Identity, restored);
        Assert.True(RepositoryCandidateTuple.TryParse(restored!.RepositoryTupleCanonical, out var tuple));
        Assert.True(goal.Repositories!.Repositories.CoversExactly(tuple));
        Assert.True(CandidateIdentity.TryParse(after.Identity.Canonical, out var parsed));
        Assert.Equal(after.Identity, parsed);
    }

    [Fact]
    public void Configure_UndeclaredGoal_PreservesExistingIdentity()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        SeedAcceptanceManifests(fixture);
        var goal = fixture.CreateGoal(declared: false);
        var home = fixture.CreateHomeWorktree(goal);
        GoalRepositoryWorktreeProvisionerFixture.CommitChange(home);
        Assert.True(GoalWorktrees.TryComputeCandidateIdentity(home, "main", out var expected, out var failure,
            projectHomeDirectory: fixture.Workspace.ProjectHomeDirectoryOrNull), failure);
        var actual = GoalWorktreeCandidateContext.Configure(fixture.Kernel, fixture.Workspace)(goal);
        Assert.True(actual.Identity is not null, actual.Failure);
        Assert.Equal(expected!.Canonical, actual.Identity!.Canonical);
    }

    [Fact]
    public void Configure_MissingSecondary_ReturnsKeyPrefixedFailure()
    {
        using var fixture = new GoalRepositoriesTestFixture();
        var goal = fixture.CreateGoal();
        fixture.CreateHomeWorktree(goal);
        var actual = GoalWorktreeCandidateContext.Configure(fixture.Kernel, fixture.Workspace)(goal);
        Assert.Null(actual.Identity);
        Assert.Equal("crosswalk:missing-worktree", actual.Failure);
    }

    private static void SeedAcceptanceManifests(GoalRepositoriesTestFixture fixture)
    {
        foreach (var repository in new[] { fixture.Home, fixture.Crosswalk })
        {
            Directory.CreateDirectory(Path.Combine(repository.CheckoutPath, "config"));
            File.WriteAllText(Path.Combine(repository.CheckoutPath, "config", "acceptance-manifest.json"), """
                {
                  "version": 1,
                  "engine": { "enforceStructuralCoverage": false },
                  "checks": [
                    { "name": "fixture check", "type": "command", "command": "dotnet", "arguments": ["--info"] }
                  ],
                  "forbiddenChangedPathGlobs": []
                }
                """);
            repository.Git("add", "config/acceptance-manifest.json");
            repository.Git("commit", "-m", "Seed acceptance manifest");
        }
    }
}
