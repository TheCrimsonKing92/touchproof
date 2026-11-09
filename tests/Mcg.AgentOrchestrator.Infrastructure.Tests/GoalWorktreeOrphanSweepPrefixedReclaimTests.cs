using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repositories, fixed clock and operation-local holder/git hooks.
public sealed class GoalWorktreeOrphanSweepPrefixedReclaimTests : GoalWorktreeTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("t-")]
    [Xunit.InlineData("c-")]
    [Xunit.InlineData("p-")]
    public void StaleDetachedTrees_OnlyUnheldReservedPrefixIsRemoved(string prefix)
    {
        var repo = CreateSeededGitRepositoryForIsolation();
        try
        {
            var stale = AddWorktree(repo, prefix + "stale");
            var held = AddWorktree(repo, prefix + "held");
            var goal = AddWorktree(repo, "abcdef12");
            var adminPath = RunGitOutput(stale, "rev-parse", "--absolute-git-dir");
            var fixture = new OrphanSweepTestFixture(repo);
            var prunes = 0;
            var removals = new List<string>();
            var holderChecks = new List<string>();
            var hooks = fixture.Hooks with
            {
                FindLockHoldersForCleanup = path =>
                {
                    holderChecks.Add(path);
                    return path == held ? [new WorktreeLockHolder(42, "gate", held)] : [];
                },
                RunWorktreeRemove = (root, timeout, path, force) =>
                {
                    removals.Add(path);
                    Assert.True(force);
                    return GoalWorktrees.DefaultRunWorktreeRemove(root, timeout, path, force);
                },
                RunWorktreePrune = (root, timeout, expire) =>
                {
                    prunes++;
                    Assert.False(expire);
                    Assert.False(Directory.Exists(stale));
                    return GoalWorktrees.DefaultRunWorktreePrune(root, timeout, expire);
                }
            };
            var before = GoalWorktreeRegistrationListing.Read(repo, hooks);
            Assert.True(before.Succeeded, before.FailureReason);
            Assert.All(new[] { stale, held, goal }, path =>
                Assert.Contains(before.Records, record => record.Path == path && record.Branch is null));

            var result = fixture.Sweep(hooks: hooks);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.Equal(stale, Assert.Single(removals));
            Assert.Equal(1, prunes);
            Assert.Contains(stale, holderChecks);
            Assert.Contains(held, holderChecks);
            Assert.DoesNotContain(goal, holderChecks);
            Assert.False(Directory.Exists(stale));
            Assert.False(Directory.Exists(adminPath));
            Assert.True(Directory.Exists(held));
            Assert.True(Directory.Exists(goal));
            var after = GoalWorktreeRegistrationListing.Read(repo, hooks);
            Assert.True(after.Succeeded, after.FailureReason);
            Assert.DoesNotContain(after.Records, record => record.Path == stale);
            Assert.Contains(after.Records, record => record.Path == held);
            Assert.Contains(after.Records, record => record.Path == goal);
            fixture.AssertOutcome(stale, "removed");
            fixture.AssertOutcome(held, "registered");
            fixture.AssertOutcome(goal, "registered");
            Assert.Empty(fixture.Deletions);
            Assert.Empty(fixture.Warnings);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void GitFailure_RemoveFailureRetains_PruneFailureStillCounts(bool failRemove)
    {
        var repo = CreateSeededGitRepositoryForIsolation();
        try
        {
            var path = AddWorktree(repo, "t-failure");
            var fixture = new OrphanSweepTestFixture(repo);
            var prunes = 0;
            var removes = 0;
            var hooks = fixture.Hooks with
            {
                FindLockHoldersForCleanup = _ => [],
                RunWorktreeRemove = (root, timeout, candidate, force) =>
                {
                    removes++;
                    return failRemove ? new(1, "", "forced removal failure") :
                        GoalWorktrees.DefaultRunWorktreeRemove(root, timeout, candidate, force);
                },
                RunWorktreePrune = (_, _, _) => { prunes++; return new(1, "", "forced prune failure"); }
            };

            var result = fixture.Sweep(hooks: hooks);

            Assert.Equal(1, removes);
            Assert.Equal(failRemove ? 0 : 1, prunes);
            Assert.Equal(failRemove ? 0 : 1, result.RemovedCount);
            Assert.Equal(failRemove, Directory.Exists(path));
            Assert.Equal(failRemove, result.LeftoverPaths.Contains(path));
            Assert.Empty(fixture.Deletions);
            fixture.AssertOutcome(path, failRemove ? "removal-failed" : "removed");
            Assert.Equal(failRemove ? "orphan-sweep-prefixed-remove" : "orphan-sweep-prefixed-prune",
                Assert.Single(fixture.Warnings).Operation);
        }
        finally { DeleteDirectory(repo); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(48)]
    [Xunit.InlineData(47)]
    public void RecentOrBoundaryTree_KeptBeforeHolderInspection(int ageHours)
    {
        var repo = CreateSeededGitRepositoryForIsolation();
        try
        {
            var path = AddWorktree(repo, "p-boundary");
            OrphanSweepTestFixture.AgeTree(path, OrphanSweepTestFixture.ReferenceTime.AddHours(-ageHours));
            var fixture = new OrphanSweepTestFixture(repo);

            var result = fixture.Sweep(hooks: fixture.Hooks with
            {
                FindLockHoldersForCleanup = _ => throw new Xunit.Sdk.XunitException("Unexpected holder inspection."),
                RunWorktreeRemove = (_, _, _, _) => throw new Xunit.Sdk.XunitException("Unexpected removal.")
            });

            Assert.Equal(0, result.RemovedCount);
            Assert.True(Directory.Exists(path));
            fixture.AssertOutcome(path, "registered");
            Assert.Empty(fixture.Warnings);
        }
        finally { DeleteDirectory(repo); }
    }

    private static string AddWorktree(string repo, string name)
    {
        var path = Path.Combine(repo, GoalWorktrees.DirectoryName, name);
        RunGit(repo, "worktree", "add", "--detach", path, "HEAD");
        OrphanSweepTestFixture.AgeTree(path, OrphanSweepTestFixture.ReferenceTime.AddDays(-3));
        return path;
    }
}
