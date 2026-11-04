using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: actual scheduler/permit/artifact boundaries, injected process and private roots.
public sealed class AcceptanceLaneWaitArtifactIntegrationTests
{
    [Fact]
    public async Task SchedulerAndPermit_WaitsSurviveOwnerDisposalAndDiskRead()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        using var held = HoldPermit(f.Root);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var overrides = new GoalAcceptanceVerifierTestOverrides
        {
            ShardPermitRootForTests = f.Root, ResolveGateShardBudgetForTests = () => 1,
            ShardPermitPollInterval = TimeSpan.FromMilliseconds(1),
            OnShardPermitWaitingForTests = _ =>
            {
                f.Clock.Advance(5);
                held.Dispose();
                waiting.TrySetResult();
            }
        };
        var verifier = new GoalAcceptanceVerifier(overrides, (string[] _, string _, CancellationToken _) =>
        {
            calls.Add("executor");
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "passed"));
        }, f.Clock);
        AcceptanceLaneWaitCollector.Occurrence? occurrence = null;
        var run = AcceptanceDotnetTestBatchScheduler.RunAsync<string, string>(
            AcceptanceDotnetTestBatchScheduler.Group(new[] { "first", "lane" }, _ => false), 1,
            Task.CompletedTask, () => true, () => false,
            (_, _, _) => throw new InvalidOperationException("Unexpected partition"),
            async (lane, token) =>
            {
                if (lane == "first") { await firstRelease.Task.WaitAsync(token); return (lane, false); }
                Assert.NotNull(occurrence);
                var result = await verifier.RunLaneTestHostWithShardPermitAsync(["dotnet", "test"], f.Root,
                    TimeSpan.FromMinutes(1), Heartbeat(f, lane), token, observation: occurrence);
                Assert.Equal(0, result.ExitCode);
                return (lane, false);
            }, TestContext.Current.CancellationToken, lane => lane == "lane" ? occurrence = f.Collector.Begin(lane) : null);
        Assert.NotNull(occurrence); // Synchronous admission attempt is blocked at the shared slot.
        Assert.Empty(calls);
        f.Clock.Advance(3);
        firstRelease.SetResult();
        await Event(waiting.Task, "permit waiting callback");
        await Event(run, "scheduler drain after permit release");
        Assert.Equal(new[] { "first", "lane" }, await run);
        Assert.Equal(new[] { "executor" }, calls);
        await f.Owner.DisposeAsync(); // Production metadata sink; no in-memory report input.
        var row = Assert.Single(f.Report().Rows);
        Assert.Equal(8, row.SummedLaneWaitSeconds);
        Assert.Equal(3, Assert.Single(row.Observations, m => m.Stage == "scheduler-slot").Seconds);
        Assert.Equal(5, Assert.Single(row.Observations, m => m.Stage == "shard-permit").Seconds);
        Assert.Equal(8, Assert.Single(row.Observations, m => m.Stage == "ready-to-admitted").Seconds);
        Assert.Equal("executor-invoked", Assert.Single(row.Observations, m => m.Stage == "admitted-to-execution").Boundary);
    }

    [Fact]
    public async Task CancelledPermit_PersistsMeasuredWaitWithoutExecution()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        using var held = HoldPermit(f.Root);
        using var cancellation = new CancellationTokenSource();
        var invoked = false;
        var wait = f.Collector.Begin("cancelled");
        var overrides = new GoalAcceptanceVerifierTestOverrides
        {
            ShardPermitRootForTests = f.Root, ResolveGateShardBudgetForTests = () => 1,
            ShardPermitPollInterval = TimeSpan.FromMilliseconds(1),
            OnShardPermitWaitingForTests = _ => { f.Clock.Advance(7); cancellation.Cancel(); }
        };
        var verifier = new GoalAcceptanceVerifier(overrides, (string[] _, string _, CancellationToken _) =>
        {
            invoked = true;
            throw new InvalidOperationException("Unadmitted executor was invoked");
        }, f.Clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.RunLaneTestHostWithShardPermitAsync(
            ["dotnet", "test"], f.Root, TimeSpan.FromMinutes(1), Heartbeat(f, "cancelled"), cancellation.Token, observation: wait));
        await f.Owner.DisposeAsync();
        Assert.False(invoked);
        var row = Assert.Single(f.Report().Rows);
        var sample = Assert.Single(row.Observations);
        Assert.Equal("shard-permit", sample.Stage);
        Assert.Equal("cancelled", sample.Boundary);
        Assert.Equal(7, sample.Seconds);
        Assert.Null(row.SummedLaneWaitSeconds);
        Assert.Equal(1, Assert.Single(row.Stages, s => s.StageName == "admitted-to-execution").Missing);
    }

    [Fact]
    public async Task RemoteOffer_SubmissionBoundarySurvivesPersistedReceipt()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        var check = RemoteLaneCostTestFixture.Lane("Measured");
        var entry = new RemoteLaneExecutorEntry("executor", 60);
        var executor = new FakeRemoteLaneExecutor
        {
            Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, "unavailable"))
        };
        using var coordinator = new RemoteLaneCoordinator(new([entry], [check.Name], null),
            new RemoteLaneCandidateIdentity(AcceptanceLaneWaitArtifactFixture.Id, "goal", "commit", "tree", "main", "manifest"),
            f.Root, f.Prefix, executor, f.Clock, TimeSpan.FromMilliseconds(1), null) { WaitCollector = f.Collector };
        await using var offers = RemoteLaneEarlyOffer.For(coordinator);
        var context = new RemoteLaneEarlyOffer.Context(_ => true, (_, _) => f.Clock.Advance(2),
            _ => TimeSpan.FromMinutes(40), _ => TimeSpan.FromMinutes(20), null, f.Clock);
        var offered = await offers.TryOfferAsync(check, context, TestContext.Current.CancellationToken);
        Assert.NotNull(offered?.Remote);
        await Event(offered.Remote, "remote submission completion");
        Assert.Single(executor.Requests);
        await f.Owner.DisposeAsync();
        var row = Assert.Single(f.Report().Rows);
        Assert.Equal("remote", row.Route);
        Assert.Equal(2, row.SummedLaneWaitSeconds);
        var execution = Assert.Single(row.Observations, m => m.Stage == "admitted-to-execution");
        Assert.Equal("submission-start", execution.Boundary);
        Assert.Equal(2, execution.Seconds);
        Assert.Equal(1, Assert.Single(row.Stages, s => s.StageName == "shard-permit").Inapplicable);
    }

    [Fact]
    public async Task SuccessfulChecks_SummaryRetainsAttemptObservations()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        var lane = f.Collector.Begin("passed"); lane.Ready(); lane.Admit(); lane.Execute();
        var verification = new AcceptanceVerificationResult(true, false, 0, null,
            Checks: [new("passed", true, 0, null)])
        { AttemptId = AcceptanceLaneWaitArtifactFixture.Id, LaneWaits = f.Collector.Snapshot() };
        var summary = AcceptanceVerificationSummary.FromVerification(verification, [], [], "commit", "main", [], [], null);
        Assert.Empty(summary.UnmetCriteria);
        Assert.Same(verification.LaneWaits, summary.LaneWaits);
        File.Delete(f.Attempt.MetadataPath);
        File.WriteAllText(f.Attempt.ResultPath, System.Text.Json.JsonSerializer.Serialize(new { acceptance = summary }, AcceptanceLaneWaitArtifactFixture.Json));
        Assert.Equal(0, Assert.Single(f.Report().Rows).SummedLaneWaitSeconds);
    }

    private static GateShardPermit HoldPermit(string root)
    {
        Assert.True(GateShardPermitPool.ForRoot(root, 1).TryAcquire(out var permit));
        return Assert.IsType<GateShardPermit>(permit);
    }

    [Fact]
    public async Task RetriedLane_PersistsTwoAuthoritativeInvocationOccurrences()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        var calls = 0;
        var overrides = new GoalAcceptanceVerifierTestOverrides
        {
            ShardPermitRootForTests = f.Root, ResolveGateShardBudgetForTests = () => 1,
            OnShardPermitAcquiredForTests = _ => f.Clock.Advance(2)
        };
        var verifier = new GoalAcceptanceVerifier(overrides, (string[] _, string _, CancellationToken _) =>
        {
            calls++;
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "passed"));
        }, f.Clock);
        for (var retry = 0; retry < 2; retry++)
        {
            var invocation = f.Owner.CreateInvocation(AcceptanceLaneWaitArtifactFixture.Id + ".lane");
            Assert.Equal(retry, invocation.Ordinal);
            var wait = f.Collector.BindLocal("lane", invocation);
            await verifier.RunLaneTestHostWithShardPermitAsync(["dotnet", "test"], f.Root,
                TimeSpan.FromMinutes(1), Heartbeat(f, "lane"), TestContext.Current.CancellationToken, observation: wait);
        }
        await f.Owner.DisposeAsync();
        Assert.Equal(2, calls);
        var report = f.Report();
        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(new[] { 0, 1 }, report.Rows.Select(row => row.Retry));
        Assert.Equal(4, report.SummedLaneWaitSeconds);
        Assert.All(report.Rows, row => Assert.Equal(2, row.SummedLaneWaitSeconds));
    }

    [Fact]
    public async Task FailedPersistenceSink_CannotReplaceCompletedOwnerTeardown()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        var calls = 0;
        var owner = new AcceptanceAttemptExecutionOwner(f.Owner.Identity, new AcceptanceGateEngineSettings(),
            options: new(LaneWaitSink: _ => { calls++; throw new IOException("optional sink failure"); }));
        owner.GetLaneWaitCollector(f.Clock).Begin("lane");
        await owner.DisposeAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BuildCommand_DoesNotManufactureLaneAdmissionOrExecution()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        var wait = f.Collector.Begin("prebuild");
        var calls = 0;
        var verifier = new GoalAcceptanceVerifier((string[] _, string _, CancellationToken _) =>
        {
            calls++;
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "built"));
        }, f.Clock);
        await verifier.RunLaneTestHostWithShardPermitAsync(["dotnet", "build"], f.Root,
            TimeSpan.FromMinutes(1), Heartbeat(f, "build"), TestContext.Current.CancellationToken, observation: wait);
        Assert.Equal(1, calls);
        Assert.Empty(Assert.Single(f.Collector.Snapshot()).Measurements);
    }

    [Fact]
    public async Task FaultedExecutor_PersistsCompletedAdmissionWithoutChangingException()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        var expected = new IOException("executor failure");
        var verifier = new GoalAcceptanceVerifier((string[] _, string _, CancellationToken _) => throw expected, f.Clock);
        var wait = f.Collector.Begin("fault");
        var actual = await Assert.ThrowsAsync<IOException>(() => verifier.RunLaneTestHostWithShardPermitAsync(
            ["dotnet", "test"], f.Root, TimeSpan.FromMinutes(1), Heartbeat(f, "fault"),
            TestContext.Current.CancellationToken, observation: wait));
        Assert.Same(expected, actual);
        await f.Owner.DisposeAsync();
        Assert.Equal(0, Assert.Single(f.Report().Rows).SummedLaneWaitSeconds);
    }
    private static GateHeartbeatContext Heartbeat(AcceptanceLaneWaitArtifactFixture f, string lane) =>
        new("goal", "test", lane, null, Path.Combine(f.Root, lane + ".heartbeat.json"), null, "dotnet test", null);
    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        catch (TimeoutException) { throw new TimeoutException("Hang guard: missing " + name); }
    }
}
