using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repository and injected batch runner.
public sealed class BatchedMergeTreeFallbackTests
{
    [Theory]
    [InlineData("exit")]
    [InlineData("not-started")]
    [InlineData("truncated")]
    [InlineData("drain")]
    public void FailedBatch_FallsBackWithIndividualResultParity(string failure)
    {
        using var fixture = new AdditiveConflictGitFixture();
        var candidates = BatchedMergeTreeStatusTests.Candidates(fixture);
        var context = new WorkerGitContext();
        var batchCalls = 0;
        var fallback = context.ReadBatchedMergeTreeStatus(fixture.Repository, fixture.MainHead, candidates,
            (_, _, input, args) =>
            {
                batchCalls++;
                Assert.Equal(3, input.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
                Assert.Contains("--stdin", args);
                return new GitCli.GitResult(failure == "exit" ? 128 : 0,
                    string.Concat(Enumerable.Repeat($"1\0{fixture.MainHead}\0\0", failure == "truncated" ? 1 : 3)), "forced batch failure")
                { ProcessStarted = failure != "not-started", DrainTimedOut = failure == "drain" };
            });
        Assert.Equal(1, batchCalls);
        Assert.Equal(failure == "not-started" ? 3 : 4, fallback.Launches);
        for (var index = 0; index < candidates.Length; index++)
        {
            var reference = context.ReadReviewerMergeTreeStatus(fixture.Repository, fixture.MainHead, candidates[index]);
            Assert.Equal(reference.IsClean, fallback.Results[index].IsClean);
            Assert.Equal(reference.ConflictPaths, fallback.Results[index].ConflictPaths);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteFallback_ThrowsInsteadOfPublishingCleanFacts(bool drainTimedOut)
    {
        using var fixture = new AdditiveConflictGitFixture();
        var fallbackCalls = 0;
        var context = new WorkerGitContext((_, _, _) =>
        {
            fallbackCalls++;
            return new(0, drainTimedOut ? fixture.MainHead : "not-a-tree", string.Empty, DrainTimedOut: drainTimedOut);
        });
        Assert.Throws<ReviewerMergeTreeStatusException>(() => context.ReadBatchedMergeTreeStatus(
            fixture.Repository, fixture.MainHead, [fixture.OriginalHead], (_, _, _, _) => new(128, "", "forced failure")));
        Assert.Equal(1, fallbackCalls);
    }
}
