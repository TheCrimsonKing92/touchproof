using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: a private repository owns the real git lock; all process checks are injected.
public sealed class GoalWorktreeOrphanSweepLockedPrefixTests : GoalWorktreeTestBase
{
    [Xunit.Fact]
    public void StaleLockedComposition_KeeptWithoutAttemptingRemoval()
    {
        var repo = CreateSeededGitRepositoryForIsolation();
        try
        {
            var path = Path.Combine(repo, GoalWorktrees.DirectoryName, "c-locked");
            RunGit(repo, "worktree", "add", "--detach", path, "HEAD");
            RunGit(repo, "worktree", "lock", "--reason", "live operator lock", path);
            OrphanSweepTestFixture.AgeTree(path, OrphanSweepTestFixture.ReferenceTime.AddDays(-3));
            var fixture = new OrphanSweepTestFixture(repo);
            var hooks = fixture.Hooks with
            {
                FindLockHoldersForCleanup = _ => [],
                RunWorktreeRemove = (_, _, _, _) => throw new Xunit.Sdk.XunitException("Locked worktree removal attempted.")
            };
            Assert.Contains(GoalWorktreeRegistrationListing.Read(repo, hooks).Records,
                record => record.Path == path && record.Locked);

            var result = fixture.Sweep(hooks: hooks);

            Assert.Equal(0, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.Contains(GoalWorktreeRegistrationListing.Read(repo, hooks).Records,
                record => record.Path == path && record.Locked);
            fixture.AssertOutcome(path, "registered");
            Assert.Empty(fixture.Deletions);
            Assert.Empty(fixture.Warnings);
        }
        finally { DeleteDirectory(repo); }
    }
}
