using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: collectors, clocks, owners and scheduler gates are fixture-local.
public sealed class AcceptanceLaneWaitObservationTests
{
    [Theory]
    [InlineData("local", "executor-invoked")]
    [InlineData("remote", "submission-start")]
    public void Grants_CompleteAdmission_UsesMonotonicSeconds(string route, string boundary)
    {
        var clock = new AcceptanceLaneWaitTestClock();
        var collector = new AcceptanceLaneWaitCollector("attempt", clock);
        var lane = collector.Begin("lane", route);
        clock.Advance(100); // Pending prerequisites are excluded.
        lane.Ready();
        lane.Start("scheduler-resource-keys");
        clock.Advance(3);
        clock.Utc = clock.Utc.AddDays(-7);
        lane.End("scheduler-resource-keys", "scheduler-and-keys-granted");
        lane.Start("shard-permit");
        clock.Advance(5);
        Assert.DoesNotContain(Assert.Single(collector.Snapshot()).Measurements, m => m.Stage == "ready-to-admitted");
        lane.End("shard-permit", "permit-acquired");
        lane.Admit();
        clock.Advance(2);
        lane.Execute();
        var row = Assert.Single(collector.Snapshot());
        Assert.Equal(8, Sample(row, "ready-to-admitted").Seconds);
        Assert.Equal(2, Sample(row, "admitted-to-execution").Seconds);
        Assert.Equal(boundary, Sample(row, "admitted-to-execution").Boundary);
        Assert.Equal(3, Sample(row, "scheduler-resource-keys").Seconds);
        Assert.Equal(5, Sample(row, "shard-permit").Seconds);
        Assert.Single(row.Measurements.Select(m => m.ClockDomain).Distinct());
        Assert.All(row.Measurements, m => Assert.Equal("local-monotonic", m.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelledWait_KeepsCompletedComponents_LeavesTransitionsUnknown(bool admitted)
    {
        var clock = new AcceptanceLaneWaitTestClock();
        var collector = new AcceptanceLaneWaitCollector("attempt", clock);
        var lane = collector.Begin("lane");
        lane.Ready();
        lane.Start("scheduler-slot");
        clock.Advance(4);
        if (admitted) { lane.End("scheduler-slot", "slot-acquired"); lane.Admit(); }
        collector.Stop("cancelled");
        var row = Assert.Single(collector.Snapshot());
        Assert.Equal(4, Sample(row, "scheduler-slot").Seconds);
        Assert.Equal(admitted ? "slot-acquired" : "cancelled", Sample(row, "scheduler-slot").Boundary);
        Assert.Equal(admitted, row.Measurements.Any(m => m.Stage == "ready-to-admitted"));
        Assert.DoesNotContain(row.Measurements, m => m.Stage == "admitted-to-execution");
    }

    [Fact]
    public void RetriesAndFallback_BindAuthoritativeInvocations_StaySeparate()
    {
        var clock = new AcceptanceLaneWaitTestClock();
        var collector = new AcceptanceLaneWaitCollector("attempt", clock);
        var pending = collector.Begin("lane");
        pending.Ready();
        Assert.Same(pending, collector.BindLocal("lane", new("attempt:lane:0", "lane", 0)));
        collector.BindLocal("lane", new("attempt:lane:1", "lane", 1));
        collector.Begin("lane", "remote");
        var rows = collector.Snapshot();
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Select(r => r.OccurrenceId).Distinct().Count());
        Assert.Equal(new[] { 0, 1 }, rows.Where(r => r.Route == "local").Select(r => r.Retry));
        Assert.All(rows, r => Assert.Equal("attempt", r.AttemptId));
        var nextAttempt = new AcceptanceLaneWaitCollector("next", clock);
        nextAttempt.BindLocal("lane", new("next:lane:0", "lane", 0));
        Assert.Equal("next", Assert.Single(nextAttempt.Snapshot()).AttemptId);
    }

    [Fact]
    public void RemotePhases_ReusesValues_LeavesCrossClockEvidenceUnknown()
    {
        var collector = new AcceptanceLaneWaitCollector("attempt", new AcceptanceLaneWaitTestClock());
        var lane = collector.Begin("lane", "remote");
        lane.RemotePhases(new(SubmitSeconds: 2, QueueSeconds: 99, RunnerSeconds: 20,
            WorktreeSeconds: 3, BuildWaitSeconds: 4, BuildSeconds: 5, TestSeconds: 8, CollectSeconds: 12));
        var row = Assert.Single(collector.Snapshot());
        Assert.Equal(4, Sample(row, "remote-build-wait").Seconds);
        Assert.Equal("remote-status-duration", Sample(row, "remote-build-wait").Source);
        Assert.Null(Sample(row, "remote-build-wait").ClockDomain);
        Assert.Null(Sample(row, "remote-queue").Seconds);
        Assert.Null(Sample(row, "remote-collect").Seconds);
    }

    [Fact]
    public async Task FailedObserver_PreservesSchedulerOrderAndVerdict()
    {
        var calls = new List<string>();
        var groups = AcceptanceDotnetTestBatchScheduler.Group(new[] { "a", "b" }, _ => false);
        var result = await AcceptanceDotnetTestBatchScheduler.RunAsync<string, string>(groups, 1,
            Task.CompletedTask, () => true, () => false,
            (_, _, _) => throw new InvalidOperationException("Unexpected partition"),
            (check, _) => { calls.Add(check); return Task.FromResult((check, false)); },
            TestContext.Current.CancellationToken, _ => throw new IOException("diagnostic sink failure"));
        Assert.Equal(new[] { "a", "b" }, calls);
        Assert.Equal(calls, result);
    }

    [Fact]
    public void BrokenClock_LeavesUnknownEvidence_DoesNotThrow()
    {
        var collector = new AcceptanceLaneWaitCollector("attempt", new BrokenClock());
        var lane = collector.Begin("lane");
        lane.Ready(); lane.Start("shard-permit"); lane.Admit(); lane.Execute(); lane.Stop("cancelled");
        var row = Assert.Single(collector.Snapshot());
        Assert.Empty(row.Measurements);
        Assert.Contains("shard-permit", row.ApplicableStages);
    }

    private static AcceptanceLaneWaitMeasurement Sample(AcceptanceLaneWaitOccurrence row, string stage) =>
        Assert.Single(row.Measurements, m => m.Stage == stage);

    [Fact]
    public async Task CancelledSchedulerWait_DrainsOwner_LeavesExecutionUnobserved()
    {
        using var cancellation = new CancellationTokenSource();
        var waitEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new AcceptanceLaneWaitTestClock(() =>
        {
            if (cancellation.IsCancellationRequested) waitEnded.TrySetResult();
        });
        var collector = new AcceptanceLaneWaitCollector("attempt", clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var run = AcceptanceDotnetTestBatchScheduler.RunAsync<string, string>(
            AcceptanceDotnetTestBatchScheduler.Group(new[] { "owner", "pending" }, _ => false),
            1, Task.CompletedTask, () => true, () => false,
            (_, _, _) => throw new InvalidOperationException("Unexpected partition"),
            // Keep the owner's slot until the pending waiter has observed cancellation.
            // Cancelling the owner's wait here could legitimately grant that slot first.
            async (check, _) => { calls.Add(check); await release.Task; return (check, false); },
            cancellation.Token, check => collector.Begin(check));
        try
        {
            Assert.Empty(Assert.Single(collector.Snapshot(), row => row.Lane == "pending").Measurements);
            Assert.Equal(new[] { "owner" }, calls);
            clock.Advance(9);
            cancellation.Cancel();
            try
            {
                await waitEnded.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException("Hang guard: pending scheduler wait did not observe cancellation.", exception);
            }
            // Snapshot takes the collector lock, so the clock signal's measurement is now published.
            var row = Assert.Single(collector.Snapshot(), row => row.Lane == "pending");
            var sample = Assert.Single(row.Measurements);
            Assert.Equal("scheduler-slot", sample.Stage);
            Assert.Equal("cancelled", sample.Boundary);
            Assert.Equal(9, sample.Seconds);
            Assert.Equal(new[] { "owner" }, calls);
            Assert.False(run.IsCompleted); // The held owner must still be drained.
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try { await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
            catch (OperationCanceledException) when (run.IsCompleted) { }
        }
    }

    private sealed class BrokenClock : TimeProvider
    {
        public override long GetTimestamp() => throw new InvalidOperationException("clock unavailable");
    }
}
