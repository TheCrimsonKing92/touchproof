using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static GoalWorktreeTestsRebaseRegistryAwareMerge;
using GitLaunches = GoalRepositoryWorktreeProvisionerFixtureSeedTests.GitLaunches;

// Parallel-safe: each fact owns isolated repositories/worktrees and an AsyncLocal launch counter.
public sealed class AdditiveConflictGitFixtureLaunchTests
{
    [Xunit.Fact]
    public void Constructor_WarmDefault_LaunchesSixGitProbes()
    {
        using var warm = new AdditiveConflictGitFixture();
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        using var fixture = new AdditiveConflictGitFixture();
        Assert.Equal(6, launches.Count);
    }

    [Xunit.Fact]
    public void Constructor_TwoFilesAndAttributes_LaunchesSixAndPreservesCommits()
    {
        using var warm = new AdditiveConflictGitFixture();
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        using var fixture = new AdditiveConflictGitFixture(
            files: [
                new(WidgetPath, BaseText, MainText, GoalText),
                new("src/Other Widget.cs", "base\n", "main\n", "goal\n")
            ],
            attributes: "*.cs text\n");
        Assert.Equal(6, launches.Count);
        // Only construction is measured; the independent history probes are outside the counter.
        launches.Dispose();

        Assert.Equal("3", Git(fixture.Worktree, "rev-list", "--count", "HEAD").Trim());
        Assert.Equal("3", Git(fixture.Repository, "rev-list", "--count", "HEAD").Trim());
    }

    [Xunit.Fact]
    public void Constructor_Default_MatchesGitRepositoryState()
    {
        using var warm = new AdditiveConflictGitFixture();
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        using var fixture = new AdditiveConflictGitFixture();
        Assert.Equal(6, launches.Count);
        launches.Dispose();

        Assert.Equal(fixture.OriginalHead, Git(fixture.Worktree, "rev-parse", "HEAD").Trim());
        Assert.Equal(fixture.MainHead, Git(fixture.Repository, "rev-parse", "HEAD").Trim());
        foreach (var root in new[] { fixture.Repository, fixture.Worktree })
        {
            Assert.Equal("false", Git(root, "config", "--local", "--get", "core.autocrlf").Trim());
            Assert.Equal("false", Git(root, "config", "--local", "--get", "commit.gpgSign").Trim());
            Assert.Empty(Git(root, "status", "--porcelain=v1"));
        }
        Assert.Equal(GoalWorktrees.BranchName(fixture.GoalId),
            Git(fixture.Worktree, "symbolic-ref", "--short", "HEAD").Trim());
    }

    [Xunit.Fact]
    public void Constructor_IgnoredPath_ForceStagesFixtureInput()
    {
        const string ignoredPath = ".orchestrator/Widget.cs";
        using var warm = new AdditiveConflictGitFixture();
        using var launches = InfrastructureTestSupport.CountGitProbeLaunches();
        using var fixture = new AdditiveConflictGitFixture(
            files: [new(ignoredPath, BaseText, MainText, GoalText)]);
        Assert.Equal(6, launches.Count);
        launches.Dispose();

        Assert.Equal(ignoredPath, Git(fixture.Repository, "check-ignore", "--no-index", "--", ignoredPath).Trim());
        Assert.Contains(ignoredPath,
            Git(fixture.Worktree, "ls-tree", "-r", "--name-only", "HEAD")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    [Xunit.Fact]
    public void MergedAdditionsAndRemoval_AssertionHelpersCutLaunches()
    {
        // Source-derived baseline helpers at 7703ca63b: 6 (log x3, rev-list, status, restore).
        // Optimized ceiling: 4 (log, rev-list, status, restore). Acceptance executes
        // the before/after measurements. Product rebase launches are unchanged;
        // the combined baseline is the observed rebase count + 6, excluding construction.
        // The funnel now serves fixture shapes in process: the base commit is served;
        // the linked-worktree commit and the large registry rewrite still launch git.
        const int maximumHelperLaunches = 4;
        using var warm = new AdditiveConflictGitFixture();
        var construction = new GitLaunches();
        AdditiveConflictGitFixture fixture;
        using (construction)
            fixture = AdditionsAndRemovalFixture();
        using (fixture)
        {
            var rebase = new GitLaunches();
            GoalWorktreeRebaseResult result;
            List<string> events;
            using (rebase)
                (result, events) = Rebase(fixture);
            var helpers = new GitLaunches();
            using (helpers)
                AssertAdditionsAndRemoval(fixture, result, events);

            AssertLaunchBudget(construction.Commands, rebase.Commands, helpers.Commands, maximumHelperLaunches,
                expectedConstructionCommitLaunches: 2);
            var commands = helpers.Commands;
            Assert.Equal(1, commands.Count(command => command == "log"));
            Assert.Equal(1, commands.Count(command => command == "rev-list"));
            Assert.Equal(1, commands.Count(command => command == "status"));
            Assert.Equal(1, commands.Count(command => command.StartsWith("--work-tree=", StringComparison.Ordinal)));
            Assert.Equal(0, commands.Count(command => command == "rev-parse"));
            Assert.Equal(0, commands.Count(command => command == "symbolic-ref"));
        }
    }

    [Xunit.Fact]
    public void RefusedBelowMeasuredRow_RestorationChecksCutLaunches()
    {
        // Source-derived baseline helpers at 7703ca63b: 5 (rev-parse x3, status, symbolic-ref).
        // Optimized ceiling: 1 (status). Acceptance executes the before/after
        // measurements. Product rebase launches are unchanged; the combined baseline
        // is the observed rebase count + 5, excluding construction.
        // The base commit is served in process. The linked-worktree commit and the
        // main commit's two-line registry replacement fall through to real git.
        const int maximumHelperLaunches = 1;
        using var warm = new AdditiveConflictGitFixture();
        var construction = new GitLaunches();
        AdditiveConflictGitFixture fixture;
        using (construction)
            fixture = BelowMeasuredRowFixture();
        using (fixture)
        {
            var rebase = new GitLaunches();
            GoalWorktreeRebaseResult result;
            List<string> events;
            using (rebase)
                (result, events) = Rebase(fixture);
            var helpers = new GitLaunches();
            using (helpers)
                AssertRefusal(fixture, result, events, "registry-ceiling-below-measured");

            AssertLaunchBudget(construction.Commands, rebase.Commands, helpers.Commands, maximumHelperLaunches,
                expectedConstructionCommitLaunches: 2);
            var commands = helpers.Commands;
            Assert.Equal(1, commands.Count(command => command == "status"));
            Assert.Equal(0, commands.Count(command => command == "rev-parse"));
            Assert.Equal(0, commands.Count(command => command == "symbolic-ref"));
            Assert.Equal(0, commands.Count(command => command == "branch"));
        }
    }

    private static void AssertLaunchBudget(
        string[] construction, string[] rebase, string[] helpers, int maximumHelperLaunches,
        int expectedConstructionCommitLaunches)
    {
        var combined = rebase.Length + helpers.Length;
        var maximumCombined = rebase.Length + maximumHelperLaunches;
        var phases = $"construction ({construction.Length}): {string.Join(", ", construction)}; " +
            $"rebase ({rebase.Length}): {string.Join(", ", rebase)}; " +
            $"helpers ({helpers.Length}): {string.Join(", ", helpers)}";
        Xunit.TestContext.Current.TestOutputHelper!.WriteLine(phases);
        Assert.True(combined <= maximumCombined,
            $"Expected at most {maximumCombined} rebase + assertion git launches; got {combined}; {phases}");
        Assert.Equal(expectedConstructionCommitLaunches, construction.Count(command => command == "commit"));
        Assert.Contains("rebase", rebase);
    }
}
