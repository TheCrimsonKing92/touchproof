using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RemoteLaneConcurrencyDeadlineTests
{
    [Theory]
    [InlineData(600, 5, 2400, 900)]
    [InlineData(600, 4, 2400, 1200)]
    [InlineData(100, 5, 2400, 300)]
    [InlineData(2000, 5, 2400, 2400)]
    [InlineData(600, 5, 600, 600)]
    public void MatchingCohortUsesMarginFloorAndCap(double median, int samples, double timeout, double expected)
    {
        Assert.Equal(TimeSpan.FromSeconds(expected), RemoteLaneDeadline.ResolveAdmitted(
            TimeSpan.FromSeconds(1200), median, samples, TimeSpan.FromSeconds(timeout)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidCohortUsesFallback(double median) => Assert.Equal(TimeSpan.FromSeconds(1200),
        RemoteLaneDeadline.ResolveAdmitted(TimeSpan.FromSeconds(1200), median, 5, TimeSpan.FromMinutes(40)));

    [Fact]
    public async Task SubFloorTimeoutDeclinesBeforeSubmissionAndReleasesCost()
    {
        using var fixture = new RemoteLaneCostTestFixture();
        Assert.NotNull(await fixture.Claim("A"));
        var outcome = await fixture.Run("A", timeoutSeconds: 299);
        Assert.Null(outcome.Accepted); Assert.Null(outcome.Handle);
        Assert.Empty(fixture.Executor.Requests);
        var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger));
        Assert.Equal(RemoteLaneOutcomeCode.OfferSkippedNoExecutor, row.Outcome);
        Assert.Equal("check-timeout-below-deadline-floor", row.Reason);
        Assert.NotNull(await fixture.Claim("B")); Assert.NotNull(await fixture.Claim("C"));
        Assert.Null(await fixture.Claim("D"));
    }

    [Fact]
    public void HistoryMatchesExecutorLaneFilterAndConcurrencyWithOldRowsExcluded()
    {
        var sample = RemoteLaneCostTestFixture.Sample("A", concurrency: 2);
        var history = new RemoteLaneAdmissionHistory([
            sample, sample with { Attempt = Detail(800) },
            sample with { ExecutorId = "other", Attempt = Detail(9000) },
            sample with { Lane = "other", Attempt = Detail(9000) },
            sample with { Expected = sample.Expected with { Filter = "other" }, Attempt = Detail(9000) },
            sample with { Admission = sample.Admission! with { Concurrency = 3 }, Attempt = Detail(9000) },
            sample with { Admission = null, Attempt = Detail(9000) },
            sample with { Outcome = RemoteLaneOutcomeCode.LaneTimeout, Attempt = Detail(9000) },
            sample with { Attempt = Detail(-1) }, sample with { Attempt = null }
        ]);
        Assert.Equal(((double?)700, 2), history.MatchingDuration(sample.Lane, sample.Expected.Filter, "executor", 2));
    }

    [Fact]
    public void HistoryUsesSubmissionThroughFetchInsteadOfStatusWhenAvailable()
    {
        var sample = RemoteLaneCostTestFixture.Sample("A", duration: 9000, concurrency: 1);
        var start = DateTimeOffset.UnixEpoch;
        var detail = sample.Attempt! with
        {
            Steps = [new("submit", 0, false, start, start.AddSeconds(10), "")],
            Fetches = [new("fetch", 0, false, start.AddSeconds(500), start.AddSeconds(600), "")]
        };
        Assert.Equal(((double?)600, 1), new RemoteLaneAdmissionHistory([sample with { Attempt = detail }])
            .MatchingDuration(sample.Lane, sample.Expected.Filter, "executor", 1));
    }

    [Fact]
    public async Task CoordinatorFreezesMatchingDeadlineWhenPeerReleases()
    {
        var samples = new[] { 550, 580, 600, 620, 650 }
            .Select(duration => RemoteLaneCostTestFixture.Sample("A", duration: duration, concurrency: 2));
        using var fixture = new RemoteLaneCostTestFixture(samples);
        Assert.NotNull(await fixture.Claim("Peer")); Assert.NotNull(await fixture.Claim("A"));
        var handle = new FakeRemoteLaneExecutor.Handle(fixture.Clock.GetUtcNow());
        var atDeadline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = 0;
        handle.OnPolled = () =>
        {
            if (Interlocked.Increment(ref polls) == 1) fixture.Clock.Advance(TimeSpan.FromSeconds(900));
            else atDeadline.TrySetResult();
        };
        fixture.Executor.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(handle));
        using var stop = new CancellationTokenSource();
        var run = fixture.Run("A", stop.Token);
        try
        {
            await RemoteLaneCostTestFixture.Event(atDeadline.Task, "poll at matching deadline");
            fixture.Coordinator.Release("executor", RemoteLaneCostTestFixture.Lane("Peer").Name);
            Assert.False(run.IsCompleted);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await RemoteLaneCostTestFixture.Event(run, "matching deadline timeout");
            var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(fixture.Ledger));
            Assert.Equal(RemoteLaneOutcomeCode.LaneTimeout, row.Outcome);
            Assert.Equal(2, row.Admission!.Concurrency);
        }
        finally
        {
            stop.Cancel();
            try { await RemoteLaneCostTestFixture.Event(run, "remote run cleanup"); }
            catch (OperationCanceledException) { }
        }
    }

    private static RemoteLaneAttemptDetail Detail(double seconds) =>
        RemoteLaneCostTestFixture.Sample("A", duration: seconds).Attempt!;
}
