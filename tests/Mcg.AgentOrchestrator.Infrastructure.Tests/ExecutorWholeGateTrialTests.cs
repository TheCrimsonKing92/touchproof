using System.Collections.Concurrent;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = ExecutorScreeningRunnerTests.ExecutorScreeningFixture;

// Parallel-safe. The executor, sampler, clock and receipt store belong to the fixture.
public sealed class ExecutorWholeGateTrialTests
{
    private const string FailedMethodFilter = "Name~Suite.ExampleTests.Broken";

    [Fact]
    public async Task SendsEveryLaneAndSerializesDifferentMachineLocalKeysWhileBackfilling()
    {
        using var fixture = new Fixture();
        var lanes = new[]
        {
            Lane("local-a", 600, "XUNIT:ENVMUTATION"),
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
        Assert.Equal(new[] { "local-a", "other-key", "build-change" },
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
                handle.Publish(fixture.Result(request, failure: request.Filter is "other-key" or FailedMethodFilter));
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
        Assert.Equal(lanes.Select(lane => lane.Filter).Append(FailedMethodFilter).Order(),
            fixture.Executor.Requests.Select(request => request.Filter).Order());
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
        Assert.Equal(AcceptanceLaneRerunEvidence.ConfirmedFailure, failing.Rerun!.Outcome);
        fixture.AssertReleased();

        void Publish(string filter)
        {
            var lane = handles[filter];
            lane.Handle.Publish(fixture.Result(lane.Request));
        }
    }

    [Fact]
    public async Task ExclusiveKeysWithDifferentCasingSerializeWhileAnotherLaneBackfills()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var first = new FakeRemoteLaneExecutor.Handle(fixture.Clock.GetUtcNow());
        var firstTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Submit = (request, _) =>
        {
            if (fixture.Executor.Requests.Count == 2) firstTwo.TrySetResult();
            return Task.FromResult(request.Filter == "A" ? new RemoteLaneSubmission(first) : fixture.PassingSubmission(request));
        };
        var plan = Plan([Lane("A", 3, "shared-key"), Lane("B", 2, "SHARED-KEY"), Lane("C", 1)]);
        var work = ExecutorScreeningRunner.RunWholeGateAsync(fixture.Candidate, fixture.Entry, plan,
            fixture.Configuration, fixture.Executor, fixture.Root, fixture.ReceiptPath, "exclusive-keys",
            fixture.Clock, TimeSpan.Zero, null, null, cancellation.Token);
        try
        {
            await firstTwo.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(new[] { "A", "C" }, fixture.Executor.Requests.Select(request => request.Filter));
            first.Publish(fixture.Result(fixture.Executor.Requests.First()));
            Assert.Equal(ExecutorWholeGateTrialStatus.Complete, (await work.WaitAsync(TimeSpan.FromSeconds(30)))!.Status);
            Assert.Equal(new[] { "A", "C", "B" }, fixture.Executor.Requests.Select(request => request.Filter));
        }
        finally
        {
            cancellation.Cancel();
            await work.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task RetriesOnlyFailuresEligibleUnderAcceptancePolicy(bool incomplete, bool explained, bool shouldRerun)
    {
        using var fixture = new Fixture();
        fixture.Executor.Submit = (request, _) =>
        {
            if (request.Filter == FailedMethodFilter)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(3));
                return Task.FromResult(fixture.PassingSubmission(request));
            }
            var handle = new FakeRemoteLaneExecutor.Handle(fixture.Clock.GetUtcNow());
            var result = fixture.Result(request, failure: true, incomplete: incomplete);
            if (explained)
            {
                var path = Assert.Single(result.TestResultPaths);
                var trx = XDocument.Load(path);
                trx.Descendants("Counters").Single().SetAttributeValue("passed", 1);
                trx.Descendants("Results").Single().Add(new XElement("UnitTestResult",
                    new XAttribute("testName", "Suite.ExampleTests.Passed"), new XAttribute("outcome", "Passed")));
                trx.Save(path);
            }
            handle.Publish(result);
            return Task.FromResult(new RemoteLaneSubmission(handle));
        };

        await ExecutorScreeningRunner.RunWholeGateAsync(fixture.Candidate, fixture.Entry, Plan([Lane("A", 1)]),
            fixture.Configuration, fixture.Executor, fixture.Root, fixture.ReceiptPath, "retry",
            fixture.Clock, TimeSpan.Zero, null, null);

        var lane = Assert.Single(Assert.Single(ExecutorScreeningReceiptLedger.ReadTrials(fixture.ReceiptPath)).Lanes);
        Assert.Equal("Suite.ExampleTests.Broken", Assert.Single(lane.FailingTests));
        Assert.Equal(incomplete ? AcceptanceShardCompletionPredicates.IncompleteExecution : null, lane.ExecutionFailure);
        if (!shouldRerun)
        {
            Assert.Null(lane.Rerun);
            Assert.Equal("A", Assert.Single(fixture.Executor.Requests).Filter);
            return;
        }

        var requests = fixture.Executor.Requests.ToArray();
        Assert.Equal(new[] { "A", FailedMethodFilter }, requests.Select(request => request.Filter));
        Assert.NotEqual(requests[0].Lane, requests[1].Lane);
        Assert.Equal(requests[0].ExecutorId, requests[1].ExecutorId);
        Assert.Equal(requests[0].VerifyingCommitSha, requests[1].VerifyingCommitSha);
        Assert.Equal(GoalAcceptanceVerifier.ShortHash(FailedMethodFilter), requests[1].FilterHash);
        Assert.Equal(3d, lane.WallSeconds);
        var rerun = Assert.IsType<ExecutorWholeGateTrialLaneRerun>(lane.Rerun);
        Assert.Equal(AcceptanceLaneRerunEvidence.Flake, rerun.Outcome);
        Assert.Equal(FailedMethodFilter, rerun.Filter);
        Assert.Equal((3d, 1, 0), (rerun.WallSeconds!.Value, rerun.Passed!.Value, rerun.Failed!.Value));
        Assert.Empty(rerun.FailingTests);
        Assert.Null(rerun.ExecutionFailure);
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
            TimeSpan.FromHours(1), lane.AllowsEmptySelection, new()
            {
                Name = "infrastructure tests: " + lane.Name, Type = "dotnet-test", Runner = "mtp",
                Project = ExecutorScreeningPlanner.Project, Arguments = ["--filter", lane.Filter],
                IsResolvedInfrastructureLane = true
            })).ToArray(), [], AcceptanceDeclaredTestSettings.Empty);

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
