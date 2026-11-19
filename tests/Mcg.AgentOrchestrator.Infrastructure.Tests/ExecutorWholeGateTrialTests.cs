using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = ExecutorScreeningRunnerTests.ExecutorScreeningFixture;

// Parallel-safe. The executor, sampler, clock and receipt store belong to the fixture.
public sealed class ExecutorWholeGateTrialTests
{
    [Fact]
    public async Task SendsEveryLaneAndSerializesDifferentMachineLocalKeysWhileBackfilling()
    {
        using var fixture = new Fixture();
        var lanes = new[]
        {
            Lane("local-a", 600, "xunit:EnvMutation"),
            Lane("local-b", 500, "xunit:ProcessSpawning"),
            Lane("keyless-a", 400),
            Lane("keyless-b", 300),
            Lane("other-key", 200, "host:local-only"),
            Lane("build-change", 100, "xunit:EnvMutation") with { RequiresBuildSystemChange = true }
        };
        var config = fixture.Configuration with
        {
            MachineLocalResourceKeys = ["xunit:EnvMutation", "xunit:ProcessSpawning"],
            Screening = new() { Mode = "shadow", KnownDivergentTests = ["ExampleTests.Broken"] }
        };
        Assert.Equal(new[] { "other-key", "build-change" },
            ExecutorScreeningPlanner.SelectLanes(lanes, config).Excluded.Select(lane => lane.LaneName));

        var plan = Plan(lanes);
        var handles = new ConcurrentDictionary<string, (RemoteLaneRequest Request, FakeRemoteLaneExecutor.Handle Handle)>();
        var firstTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextLocal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var machineLocal = new HashSet<string>(["local-a", "local-b", "build-change"], StringComparer.Ordinal);
        var localOverlap = false;
        fixture.Executor.Submit = (request, _) =>
        {
            var handle = new FakeRemoteLaneExecutor.Handle(fixture.Clock.GetUtcNow());
            if (machineLocal.Contains(request.Filter))
                localOverlap |= handles.Any(item => machineLocal.Contains(item.Key) && !item.Value.Handle.IsAbandoned);
            handles[request.Filter] = (request, handle);
            if (handles.Count == 2) firstTwo.TrySetResult();
            if (request.Filter == "local-b") nextLocal.TrySetResult();
            if (request.Filter is not ("local-a" or "local-b" or "keyless-a"))
                handle.Publish(fixture.Result(request, failure: request.Filter == "other-key"));
            return Task.FromResult(new RemoteLaneSubmission(handle));
        };

        var expectedStart = fixture.Clock.GetUtcNow();
        var local = new ExecutorWholeGateTrialLocalAttempt("local-attempt", fixture.Candidate.Commit, 1200,
            [new("infrastructure tests: local-a", 11, 12, 3, ["LocalTests.Broken"])]);
        var sampler = new Sampler(fixture.Clock);
        DateTimeOffset? localReadAt = null;
        using var cancellation = new CancellationTokenSource();
        var work = ExecutorScreeningRunner.RunWholeGateAsync(fixture.Candidate, fixture.Entry, plan, config,
            fixture.Executor, fixture.Root, fixture.ReceiptPath, "fixture-run", fixture.Clock, TimeSpan.Zero, sampler,
            at => { localReadAt = at; return local; }, cancellation.Token);
        try
        {
            await firstTwo.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(new[] { "local-a", "keyless-a" }, fixture.Executor.Requests.Select(request => request.Filter));
            Assert.True(RemoteExecutorOccupancy.IsOccupied(fixture.Root, fixture.Entry.Id));
            Assert.Equal(expectedStart, localReadAt);
            fixture.Clock.Advance(TimeSpan.FromSeconds(12));
            Publish("local-a");
            await nextLocal.Task.WaitAsync(TimeSpan.FromSeconds(30));
            fixture.Clock.Advance(TimeSpan.FromSeconds(7));
            Publish("local-b");
            Publish("keyless-a");
            Assert.NotNull(await work.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            cancellation.Cancel();
            await work.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.False(localOverlap);
        Assert.Equal(lanes.Select(lane => lane.Filter).Order(), fixture.Executor.Requests.Select(request => request.Filter).Order());
        Assert.All(fixture.Executor.Requests, request =>
        {
            Assert.Equal(fixture.Entry.Id, request.ExecutorId);
            Assert.Equal(fixture.Candidate.Commit, request.VerifyingCommitSha);
            Assert.StartsWith("wgt-", request.Lane);
        });
        var receipt = Assert.Single(ExecutorScreeningReceiptLedger.ReadTrials(fixture.ReceiptPath));
        Assert.Equal(2, receipt.Version);
        Assert.Equal("whole-gate-trial", receipt.Kind);
        Assert.Equal(fixture.Entry.Id, receipt.ExecutorId);
        Assert.Equal(fixture.Candidate.Commit, receipt.CandidateCommit);
        Assert.Equal(expectedStart, receipt.StartedAt);
        Assert.Equal(24, receipt.WallSeconds);
        Assert.Equal(ExecutorWholeGateTrialStatus.Complete, receipt.Status);
        Assert.Equal(new ExecutorWholeGateTrialMemory(2000, 4000, 24), receipt.Memory);
        Assert.Equal((local.AttemptId, local.CandidateCommit, local.WallSeconds),
            (receipt.LocalAttempt!.AttemptId, receipt.LocalAttempt.CandidateCommit, receipt.LocalAttempt.WallSeconds));
        var localLane = Assert.Single(receipt.LocalAttempt.Lanes);
        Assert.Equal(("infrastructure tests: local-a", 11d, 12, 3),
            (localLane.LaneName, localLane.WallSeconds!.Value, localLane.Passed!.Value, localLane.Failed!.Value));
        Assert.Equal("LocalTests.Broken", Assert.Single(localLane.FailingTests));

        Assert.Equal(lanes.Select(lane => "infrastructure tests: " + lane.Name).Order(),
            receipt.Lanes.Select(lane => lane.LaneName).Order());
        var passing = Assert.Single(receipt.Lanes, lane => lane.LaneName == "infrastructure tests: local-a");
        Assert.Equal((12d, 1, 0), (passing.WallSeconds!.Value, passing.Passed!.Value, passing.Failed!.Value));
        var failing = Assert.Single(receipt.Lanes, lane => lane.LaneName == "infrastructure tests: other-key");
        Assert.Equal((0, 1), (failing.Passed!.Value, failing.Failed!.Value));
        Assert.Equal("Suite.ExampleTests.Broken", Assert.Single(failing.FailingTests));
        fixture.AssertReleased();

        void Publish(string filter)
        {
            var lane = handles[filter];
            lane.Handle.Publish(fixture.Result(lane.Request));
        }
    }

    [Fact]
    public async Task TrialAndScreeningReadersKeepCandidateEligibilitySeparate()
    {
        using var fixture = new Fixture();
        await ExecutorScreeningRunner.RunWholeGateAsync(fixture.Candidate, fixture.Entry, Plan([Lane("A", 1)]),
            fixture.Configuration, fixture.Executor, fixture.Root, fixture.ReceiptPath, "trial",
            fixture.Clock, TimeSpan.Zero, null, null);
        var screening = new ExecutorScreeningReceipt("screen", "another-goal", fixture.Candidate.Commit,
            fixture.Candidate.Tree, fixture.Candidate.MainSha, fixture.Entry.Id, fixture.Clock.GetUtcNow(),
            fixture.Clock.GetUtcNow(), ExecutorScreeningVerdict.Pass, [], []);
        ExecutorScreeningReceiptLedger.Append(fixture.ReceiptPath, screening);
        var screeningRead = Assert.Single(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath));
        Assert.Equal("screen", screeningRead.ReceiptId);
        var trialRead = Assert.Single(ExecutorScreeningReceiptLedger.ReadTrials(fixture.ReceiptPath));
        Assert.Equal("trial", trialRead.ReceiptId);
        Assert.Equal(fixture.Candidate, ExecutorScreeningPlanner.PickCandidate([fixture.Candidate], [screeningRead]));
        Assert.Null(ExecutorScreeningPlanner.PickTrialCandidate([fixture.Candidate], [trialRead]));
    }

    [Fact]
    public async Task UnconfirmedCompletionPreventsAnotherMachineLocalSubmissionAndPersistsPartialResults()
    {
        using var fixture = new Fixture();
        var configuration = fixture.Configuration with { MachineLocalResourceKeys = ["a", "b"] };
        FakeRemoteLaneExecutor.Handle? handle = null;
        fixture.Executor.Submit = (_, _) =>
        {
            handle = new(fixture.Clock.GetUtcNow())
            {
                OnHeartbeatRead = () => fixture.Clock.Advance(TimeSpan.FromSeconds(61))
            };
            return Task.FromResult(new RemoteLaneSubmission(handle));
        };
        var receipt = await ExecutorScreeningRunner.RunWholeGateAsync(fixture.Candidate, fixture.Entry,
            Plan([Lane("A", 2, "a"), Lane("B", 1, "b")]), configuration, fixture.Executor, fixture.Root,
            fixture.ReceiptPath, "partial", fixture.Clock, TimeSpan.Zero, null, null);
        Assert.Equal("A", Assert.Single(fixture.Executor.Requests).Filter);
        Assert.True(handle!.IsAbandoned);
        Assert.Equal(ExecutorWholeGateTrialStatus.Incomplete, receipt!.Status);
        Assert.Equal(new[] { "lease-expired", "executor-completion-unconfirmed" },
            receipt.Lanes.Select(lane => lane.ExecutionFailure));
        Assert.All(receipt.Lanes, lane => { Assert.Null(lane.Passed); Assert.Null(lane.Failed); });
        Assert.Equal("partial", Assert.Single(ExecutorScreeningReceiptLedger.ReadTrials(fixture.ReceiptPath)).ReceiptId);
        fixture.AssertReleased();
    }

    private static AcceptanceTestLane Lane(string name, double duration, params string[] keys) =>
        new(name, name, duration) { ExclusiveResourceKeys = keys };

    private static ExecutorWholeGateTrialPlan Plan(IReadOnlyList<AcceptanceTestLane> lanes) =>
        new(lanes.Select(lane => new ExecutorWholeGateTrialLane("infrastructure tests: " + lane.Name,
            ExecutorScreeningPlanner.Project, lane.Filter, lane.ExclusiveResourceKeys, lane.EstimatedSerialSeconds,
            TimeSpan.FromHours(1), lane.AllowsEmptySelection)).ToArray(), [], AcceptanceDeclaredTestSettings.Empty);

    private sealed class Sampler(ManualRemoteLaneClock clock) : IExecutorWholeGateTrialSampler
    {
        public Task StartAsync(CancellationToken token)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.CompletedTask;
        }

        public Task<ExecutorWholeGateTrialMemory> StopAsync()
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(new ExecutorWholeGateTrialMemory(2000, 4000, 24));
        }
    }
}
