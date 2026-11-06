using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repository; launch observation is chained and scoped to this test context.
public sealed class BatchedMergeTreeStatusTests
{
    [Fact]
    public void ThreeCandidates_OneLaunchMatchesIndividualCleanAndConflictResults()
    {
        using var fixture = new AdditiveConflictGitFixture();
        var candidates = Candidates(fixture);
        var context = new WorkerGitContext();
        var launches = 0;
        var testName = HostCapacitySlotTimingWriter.CurrentTestName();
        var previous = Volatile.Read(ref GitCli.LaunchObserver);
        Action<string> observer = command =>
        {
            previous?.Invoke(command);
            if (command == "merge-tree" && HostCapacitySlotTimingWriter.CurrentTestName() == testName) Interlocked.Increment(ref launches);
        };
        IReadOnlyList<ReviewerMergeTreeStatus> results;
        Assert.Same(previous, Interlocked.CompareExchange(ref GitCli.LaunchObserver, observer, previous));
        try
        {
            var batch = context.ReadBatchedMergeTreeStatus(fixture.Repository, fixture.MainHead, candidates);
            results = batch.Results;
            Assert.Equal(1, batch.Launches);
            Assert.Equal(1, launches);
        }
        finally { Interlocked.CompareExchange(ref GitCli.LaunchObserver, previous, observer); }
        Assert.Equal(new[] { true, false, true }, results.Select(result => result.IsClean));
        for (var index = 0; index < candidates.Length; index++)
        {
            var reference = context.ReadReviewerMergeTreeStatus(fixture.Repository, fixture.MainHead, candidates[index]);
            Assert.Equal(reference.IsClean, results[index].IsClean);
            Assert.Equal(reference.ConflictPaths, results[index].ConflictPaths);
        }
        Assert.Equal(new[] { AdditiveConflictGitFixture.WidgetPath }, results[1].ConflictPaths);
    }

    internal static string[] Candidates(AdditiveConflictGitFixture fixture)
    {
        var tree = AdditiveConflictGitFixture.Git(fixture.Repository, "rev-parse", $"{fixture.MainHead}^{{tree}}").Trim();
        string Clean(string name) => AdditiveConflictGitFixture.Git(fixture.Repository,
            "commit-tree", tree, "-p", fixture.MainHead, "-m", name).Trim();
        return [Clean("Clean candidate one"), fixture.OriginalHead, Clean("Clean candidate two")];
    }
}
