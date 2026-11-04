using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeRecoverySerializationTests : GoalWorktreeRecoveryTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ConcurrentCallerJoinsProtectedRecovery(bool ordinaryCreation)
    {
        using var f = new MissingFixture();
        using var observed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var contenderObservedLock = new ManualResetEventSlim();
        var reads = 0;
        var first = Task.Run(() => f.Recover(() =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                observed.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "Recovery release event was not signalled.");
            }
            return null;
        }));
        Task<string>? second = null;
        try
        {
            Assert.True(observed.Wait(TimeSpan.FromSeconds(60)), "Recovery did not reach protected ownership observation.");
            second = Task.Run(() =>
            {
                using (var probe = GoalWorktreeRootLock.TryAcquire(Path.GetDirectoryName(f.Path)!, TimeSpan.Zero))
                    Assert.Null(probe); // Direct mutex observation, not elapsed-time inference.
                contenderObservedLock.Set();
                if (ordinaryCreation) return GoalWorktrees.Ensure(f.Repo, f.Goal.Id);
                var result = GoalWorktrees.RecoverMissing(f.Repo, f.Goal.Id, () => null, _ => { });
                Assert.True(result.Succeeded, result.Observation);
                Assert.False(result.Recreated);
                return result.CanonicalPath;
            });
            Assert.True(contenderObservedLock.Wait(TimeSpan.FromSeconds(60)), "Contender did not observe the held root lock.");
            release.Set();
            Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(90))).Recreated);
            Assert.Equal(f.Path, await second.WaitAsync(TimeSpan.FromSeconds(90)));
            f.AssertRestored();
            Assert.Single(f.Receipts);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(90));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(90));
        }
    }
}
