using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: overrides belong to each async flow; real git and state use a private repository.
public sealed class ScopedRemoteGitMirrorSeamTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentFlows_KeepOverlappingOverridesIsolated(bool gitRunner)
    {
        var baseline = GetOverride(gitRunner);
        var firstInstalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInstalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstFlow = Task.Run(async () =>
        {
            try
            {
                using (UseOverride(gitRunner, 101))
                {
                    AssertOverride(gitRunner, 101);
                    firstInstalled.SetResult();
                    await WaitFor(secondInstalled.Task, "second flow installed its override");
                    AssertOverride(gitRunner, 101);
                    firstChecked.SetResult();
                }
                Assert.Equal(baseline, GetOverride(gitRunner));
            }
            finally
            {
                firstInstalled.TrySetResult();
                firstChecked.TrySetResult();
            }
        });
        var secondFlow = Task.Run(async () =>
        {
            try
            {
                await WaitFor(firstInstalled.Task, "first flow installed its override");
                Assert.Equal(baseline, GetOverride(gitRunner));
                using (UseOverride(gitRunner, 202))
                {
                    AssertOverride(gitRunner, 202);
                    secondInstalled.SetResult();
                    await WaitFor(firstChecked.Task, "first flow checked its overlapping override");
                    AssertOverride(gitRunner, 202);
                }
                Assert.Equal(baseline, GetOverride(gitRunner));
            }
            finally { secondInstalled.TrySetResult(); }
        });

        await WaitFor(Task.WhenAll(firstFlow, secondFlow), "both independent override flows finished");
        Assert.Equal(baseline, GetOverride(gitRunner));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NestedScopes_RestorePreviousOverride_AndDisposeOnlyOnce(bool gitRunner)
    {
        var baseline = GetOverride(gitRunner);
        using (UseOverride(gitRunner, 101))
        {
            AssertOverride(gitRunner, 101);
            using (var inner = UseOverride(gitRunner, 202))
            {
                AssertOverride(gitRunner, 202);
                inner.Dispose();
                AssertOverride(gitRunner, 101);
                using (UseOverride(gitRunner, 303))
                {
                    inner.Dispose();
                    AssertOverride(gitRunner, 303);
                }
            }
            AssertOverride(gitRunner, 101);
        }
        Assert.Equal(baseline, GetOverride(gitRunner));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DescendantFlow_RetainsCapturedOverrideAfterParentDisposes(bool gitRunner)
    {
        var baseline = GetOverride(gitRunner);
        var parentDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task descendant;
        using (UseOverride(gitRunner, 101))
        {
            descendant = Task.Run(async () =>
            {
                await WaitFor(parentDisposed.Task, "parent disposed its scope");
                AssertOverride(gitRunner, 101);
                using (UseOverride(gitRunner, 202))
                    AssertOverride(gitRunner, 202);
                AssertOverride(gitRunner, 101);
            });
        }
        Assert.Equal(baseline, GetOverride(gitRunner));
        parentDisposed.SetResult();
        await WaitFor(descendant, "descendant checked its captured override");
        Assert.Equal(baseline, GetOverride(gitRunner));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullOverride_ThrowsAndPreservesActiveOverride(bool gitRunner)
    {
        var baseline = GetOverride(gitRunner);
        using (UseOverride(gitRunner, 101))
        {
            Assert.Throws<ArgumentNullException>(() =>
            {
                if (gitRunner) RemoteGitMirror.UseGitRunner(null!);
                else RemoteGitMirror.UseBackoffForAttempt(null!);
            });
            AssertOverride(gitRunner, 101);
        }
        Assert.Equal(baseline, GetOverride(gitRunner));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(4, 30)]
    [InlineData(20, 30)]
    public void NoOverride_UsesOriginalBackoffTable(int attempt, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), RemoteGitMirror.BackoffForAttempt(attempt));

    [Fact]
    public void Defaults_ResolveRealBranchAndDeferFailedPushByOneMinute()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (kernel, goal) = LandingExecutorTests.CreateVerifiedGoal(repo);
            var branch = GoalWorktrees.BranchName(goal.Id);
            LandingExecutorTests.AddGoalBranchCommit(repo, branch, "mirror.txt", "mirror default");
            var expected = GitCli.Run(repo, "rev-parse", branch);
            Assert.True(expected.Succeeded, expected.Error);
            var actual = RemoteGitMirror.GitRunner(repo, ["rev-parse", branch]);
            Assert.True(actual.Succeeded, actual.Error);
            Assert.Equal(expected.Output, actual.Output);

            var missingRemote = Path.Combine(repo, "missing-mirror.git");
            Assert.False(Directory.Exists(missingRemote));
            LandingExecutorTests.ReadGit(repo, "remote", "add", "mirror", missingRemote);
            Directory.CreateDirectory(Path.Combine(repo, "config"));
            File.WriteAllText(Path.Combine(repo, "config", "mirror.json"), """
                { "enabled": true, "remotes": ["mirror"],
                  "push": { "main": true, "goalBranch": false, "tags": false } }
                """);
            RemoteGitMirror.EnqueueAfterLanding(repo, goal);
            var pending = Assert.Single(RemoteGitMirror.ReadState(repo).Entries);
            Assert.Equal(expected.Output.Trim(), pending.GoalBranchCommit);
            Assert.Equal(RemoteMirrorEntryStatus.Pending, pending.Status);

            var result = RemoteGitMirror.ProcessDue(kernel, repo, goal.Id);

            Assert.Equal(RemoteMirrorOutcomeKind.MirrorFailed, Assert.Single(result.Outcomes).Kind);
            var deferred = Assert.Single(RemoteGitMirror.ReadState(repo).Entries);
            Assert.Equal(RemoteMirrorEntryStatus.Deferred, deferred.Status);
            Assert.Equal(1, deferred.Attempts);
            Assert.NotNull(deferred.NextAttemptUtc);
            Assert.Equal(TimeSpan.FromMinutes(1), deferred.NextAttemptUtc.Value - deferred.UpdatedUtc);
        }
        finally { SharedTestSupport.RemoveTempDirectory(repo); }
    }

    [Theory]
    [InlineData("UtcNow")]
    [InlineData("StatusWriter")]
    public void DeadSeams_AreRemoved(string name) =>
        Assert.Null(typeof(RemoteGitMirror).GetProperty(name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));

    [Theory]
    [InlineData("GitRunner")]
    [InlineData("BackoffForAttempt")]
    public void LiveSeams_HaveNoSetter(string name)
    {
        var property = typeof(RemoteGitMirror).GetProperty(name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.NotNull(property);
        Assert.False(property.CanWrite);
    }

    private static Delegate GetOverride(bool gitRunner) =>
        gitRunner ? RemoteGitMirror.GitRunner : RemoteGitMirror.BackoffForAttempt;

    private static IDisposable UseOverride(bool gitRunner, int marker) =>
        gitRunner
            ? RemoteGitMirror.UseGitRunner((_, _) => new GitCli.GitResult(0, $"marker-{marker}", string.Empty))
            : RemoteGitMirror.UseBackoffForAttempt(_ => TimeSpan.FromMinutes(marker));

    private static void AssertOverride(bool gitRunner, int marker)
    {
        if (gitRunner)
            Assert.Equal($"marker-{marker}", RemoteGitMirror.GitRunner("unused", ["rev-parse", "HEAD"]).Output);
        else
            Assert.Equal(TimeSpan.FromMinutes(marker), RemoteGitMirror.BackoffForAttempt(1));
    }

    private static async Task WaitFor(Task signal, string eventName)
    {
        try { await signal.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Hang waiting for event: {eventName}.", error);
        }
    }
}
