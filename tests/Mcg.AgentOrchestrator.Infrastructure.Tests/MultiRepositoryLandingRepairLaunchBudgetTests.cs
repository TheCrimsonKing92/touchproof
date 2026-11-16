using System.Text.Json;
using Xunit;
using static GoalRepositoryWorktreeProvisionerFixtureSeedTests;
using Role = MultiRepositoryAcceptanceGateSeedTemplate.RepositoryRole;

// Parallel-safe: each case owns its repositories; launch observation is AsyncLocal-filtered and chained.
public sealed class MultiRepositoryLandingRepairLaunchBudgetTests
{
    [Fact]
    public void Constructor_WarmSeed_ReducesCompleteFixtureLaunches()
    {
        // Baseline at f3b5a57f9: 14 (the existing seed-template test budget):
        // home worktree 1, crosswalk provisioning 7, change commits 3 each.
        // New per-case count/ceiling: 10, removing two add and two rev-parse launches
        // by committing each tracked path directly and discarding unused tip reads.
        // Seed initialization is one-time work, warmed separately before observation.
        // Provisioning's 7 launches and product AdvanceOnce/Repair.Run body launches
        // are unchanged. RunHome also saves two rev-parse calls per successful run,
        // outside this constructor budget. Acceptance executes the count measurements.
        const int maximumGitLaunches = 10;
        var root = SharedTestSupport.CreateTempDirectory();
        MultiRepositoryAcceptanceGateFixture? fixture = null;
        try
        {
            MultiRepositoryAcceptanceGateSeedTemplate.CopyTo(Path.Combine(root, "home"), Role.Home);
            MultiRepositoryAcceptanceGateSeedTemplate.CopyTo(Path.Combine(root, "second"), Role.Second);
            var launches = new GitLaunches();
            using (launches)
                fixture = new MultiRepositoryAcceptanceGateFixture();

            var commands = launches.Commands;
            Assert.True(commands.Length <= maximumGitLaunches,
                $"Expected at most {maximumGitLaunches} fixture git launches; got {commands.Length}: {string.Join(", ", commands)}");
            Assert.Equal(2, commands.Count(command => command == "commit"));
            // Two adds and two registration reads across the home and secondary worktrees.
            Assert.Equal(4, commands.Count(command => command == "worktree"));
        }
        finally
        {
            try { fixture?.Dispose(); }
            finally { SharedTestSupport.RemoveTempDirectory(root); }
        }
    }

    [Fact]
    public void Repositories_FourCheckouts_ReducesLaunchesAndRetainsEachHead()
    {
        // Baseline: 20 (show-ref, rev-parse, status, diff and worktree on four paths).
        // New per-snapshot count/ceiling: 12. Remove four rev-parse calls using
        // status --porcelain=v2 --branch, and duplicate show-ref/worktree calls
        // from the two linked worktrees. Keep refs, HEAD, status, diff and registrations.
        const int maximumGitLaunches = 12;
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var launches = new GitLaunches();
        string snapshot;
        using (launches)
            snapshot = MultiRepositoryLandingRepairTests.Repositories(fixture);

        var commands = launches.Commands;
        Assert.True(commands.Length <= maximumGitLaunches,
            $"Expected at most {maximumGitLaunches} snapshot git launches; got {commands.Length}: {string.Join(", ", commands)}");
        Assert.Equal(2, commands.Count(command => command == "show-ref"));
        Assert.Equal(2, commands.Count(command => command == "worktree"));
        Assert.Equal(4, commands.Count(command => command == "status"));
        Assert.Equal(4, commands.Count(command => command == "diff"));

        using var document = JsonDocument.Parse(snapshot);
        var checkouts = document.RootElement.GetProperty("Checkouts").EnumerateArray().ToArray();
        var paths = new[] { fixture.Repositories.Home.CheckoutPath, fixture.Repositories.Crosswalk.CheckoutPath,
            fixture.HomeWorktree, fixture.SecondaryWorktree };
        Assert.Equal(paths.Length, checkouts.Length);
        for (var index = 0; index < paths.Length; index++)
            Assert.Contains("# branch.oid " + RealGit(paths[index], "rev-parse", "HEAD"),
                checkouts[index].GetProperty("Status").GetString());
    }

    [Fact]
    public void Observer_RealGitCommand_CountsLaunch()
    {
        using var fixture = new GoalRepositoryWorktreeProvisionerFixture();
        var launches = new GitLaunches();
        using (launches)
            RealGit(fixture.CheckoutPath, "rev-parse", "HEAD");

        Assert.Equal(new[] { "rev-parse" }, launches.Commands);
    }
}
