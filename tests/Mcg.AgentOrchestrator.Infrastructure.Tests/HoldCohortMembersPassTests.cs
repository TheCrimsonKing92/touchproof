using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its kernel, run-completion signal, SQLite file and directory.
public sealed class HoldCohortMembersPassTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HoldPass_FiveEligibleGoals_SweepsExactlyOnce(bool runInFlight)
    {
        using var fixture = new Fixture();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (runInFlight)
            Assert.True(fixture.Driver.TryRegisterCohortGateRunForTests(
                new ConductorAcceptanceCohortSelection(fixture.Goals.Take(2).Select(Projection).ToArray(), []),
                completion));
        Assert.Equal(runInFlight ? 2 : 0, fixture.Driver.GetActiveCohortGateMemberGoalIds().Count);
        var results = new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal);
        var lines = new List<string>();
        Assert.Equal(5, fixture.Goals.Length);
        Assert.Empty(results); // Every eligible goal must reach the hold lookup.
        var sweepsBefore = fixture.Driver.CohortGateSweepCount;

        try
        {
            ConductorBatchLoop.HoldCohortMembers(fixture.Goals, results, fixture.Driver,
                ConductorAutonomyPolicy.Conservative, 7, lines);

            Assert.Equal(1, fixture.Driver.CohortGateSweepCount - sweepsBefore);
            Assert.Equal(runInFlight ? 2 : 0, results.Count);
            Assert.Equal(runInFlight ? 2 : 0, lines.Count);
            foreach (var result in results.Values)
            {
                var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Result.Outcome);
                Assert.Equal(ConductorHoldOwner.BackgroundAttempt, held.Owner);
                Assert.Null(result.SlotIndex);
            }
        }
        finally
        {
            completion.TrySetResult();
            fixture.Driver.SweepCompletedCohortGateRuns();
        }
    }

    [Fact]
    public void HoldPass_NoEligibleGoals_StillSweepsExactlyOnce()
    {
        using var fixture = new Fixture();
        var results = new Dictionary<string, ParallelLandingOutcome>();
        var lines = new List<string>();
        var sweepsBefore = fixture.Driver.CohortGateSweepCount;

        ConductorBatchLoop.HoldCohortMembers([], results, fixture.Driver,
            ConductorAutonomyPolicy.Conservative, 7, lines);

        Assert.Equal(1, fixture.Driver.CohortGateSweepCount - sweepsBefore);
        Assert.Empty(results);
        Assert.Empty(lines);
    }

    [Fact]
    public void ReadReceiptsForGoal_FiftyReceipts_DeserializesOnlyTheGoalsTwo()
    {
        using var fixture = new Fixture();
        var goalId = new GoalId(1.ToString("x32"));
        var input = new List<MergeTrainReceipt>();
        for (var index = 0; index < 50; index++)
            MergeTrainDropBacktestTests.Fixture.Add(input,
                index == 39 ? MergeTrainGateOutcome.Passed : MergeTrainGateOutcome.Failed, 10,
                [MergeTrainDropBacktestTests.Fixture.Member(index is 7 or 39 ? 1 : 200 + index),
                 MergeTrainDropBacktestTests.Fixture.Member(100 + index)]);
        // Insertion order must not accidentally satisfy completion order.
        foreach (var receipt in input.AsEnumerable().Reverse())
            MergeTrainDropBacktestTests.Fixture.Save(fixture.Store, fixture.Root, receipt);
        Assert.Equal(50, fixture.Store.ReadReceiptsSince(DateTimeOffset.MinValue).Count);
        var deserialized = 0;
        var reader = MergeTrainAcceptanceStore.OpenReadOnly(fixture.StorePath);

        var receipts = reader.ReadReceiptsForGoal(goalId, () => deserialized++);

        Assert.Equal(new[] { "receipt-008", "receipt-040" }, receipts.Select(receipt => receipt.ReceiptId));
        Assert.All(receipts, receipt => Assert.Contains(receipt.Identity.Members, member => member.GoalId == goalId));
        Assert.Equal(new[] { input[7].CompletedAt, input[39].CompletedAt }, receipts.Select(receipt => receipt.CompletedAt));
        Assert.Equal(2, deserialized);
        Assert.Equal(receipts.Select(receipt => receipt.ReceiptId),
            reader.ReadReceiptsForGoal(goalId).Select(receipt => receipt.ReceiptId));
    }

    [Fact]
    public void ReadReceiptsForGoal_GoalWithoutReceipts_ReturnsNone()
    {
        using var fixture = new Fixture();
        var input = new List<MergeTrainReceipt>();
        MergeTrainDropBacktestTests.Fixture.Add(input, MergeTrainGateOutcome.Failed, 10,
            [MergeTrainDropBacktestTests.Fixture.Member(1), MergeTrainDropBacktestTests.Fixture.Member(2)]);
        fixture.Store.SaveGateReceipt(Assert.Single(input));
        var deserialized = 0;

        var receipts = MergeTrainAcceptanceStore.OpenReadOnly(fixture.StorePath)
            .ReadReceiptsForGoal(new GoalId(3.ToString("x32")), () => deserialized++);

        Assert.Empty(receipts);
        Assert.Equal(0, deserialized);
    }

    [Fact]
    public void HoldPass_MemberEjectedAndUnheld_KeepsLiteralOutcomesAndLines()
    {
        using var fixture = new Fixture();
        var original = fixture.SeedEjectedTrain();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Driver.TryRegisterCohortGateRunForTests(
            new ConductorAcceptanceCohortSelection(fixture.Goals.Take(2).Select(Projection).ToArray(), []),
            completion));
        fixture.Now += TimeSpan.FromMinutes(3);
        var waiting = WaitingOutcome(fixture.Goals[4]);
        var results = new Dictionary<string, ParallelLandingOutcome>(StringComparer.Ordinal)
        {
            [fixture.Goals[4].Id.Value] = waiting
        };
        var lines = new List<string>();

        try
        {
            ConductorBatchLoop.HoldCohortMembers([fixture.Goals[0], fixture.Goals[4], fixture.Goals[2]],
                results, fixture.Driver, ConductorAutonomyPolicy.Conservative, 7, lines);

            Assert.Equal(2, results.Count);
            Assert.Equal(new ParallelLandingOutcome(new ConductorAdvanceResult(
                "00000000000000000000000000000001", "00000000", "Conservative",
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified,
                    "Acceptance cohort gate owns this member: outcome=inflight " +
                    "owner=00000000000000000000000000000001+00000000000000000000000000000002 " +
                    "fingerprint=00000000000000000000000000000001:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb:" +
                    "00000000000000000000000000000002:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb:" +
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa elapsed_ms=180000")
                { Owner = ConductorHoldOwner.BackgroundAttempt }), null), results[fixture.Goals[0].Id.Value]);
            Assert.Equal(waiting with
            {
                Result = waiting.Result with
                {
                    Outcome = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified,
                        $"ejected from train {original.Value} reason=RedNewestMember surviving-receipt=surviving-receipt",
                        "capacity")
                }
            }, results[fixture.Goals[4].Id.Value]);
            Assert.False(results.ContainsKey(fixture.Goals[2].Id.Value));
            Assert.Equal(new[]
            {
                "ACCEPTANCE_COHORT tick=7 goal=00000000 result=held outcome=inflight " +
                "owner=00000000000000000000000000000001+00000000000000000000000000000002 " +
                "fingerprint=00000000000000000000000000000001:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb:" +
                "00000000000000000000000000000002:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb:" +
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa elapsed_ms=180000"
            }, lines);
        }
        finally
        {
            completion.TrySetResult();
            fixture.Driver.SweepCompletedCohortGateRuns();
        }
    }

    [Fact]
    public void HoldPass_ReceiptAppendedBetweenPasses_SecondPassSeesIt()
    {
        using var fixture = new Fixture();
        var original = fixture.SeedEjectedTrain();
        var goal = fixture.Goals[4];
        var waiting = WaitingOutcome(goal);
        var results = new Dictionary<string, ParallelLandingOutcome> { [goal.Id.Value] = waiting };
        var lines = new List<string>();

        ConductorBatchLoop.HoldCohortMembers([goal], results, fixture.Driver,
            ConductorAutonomyPolicy.Conservative, 7, lines);

        var first = Assert.IsType<ConductorAdvanceOutcome.Held>(results[goal.Id.Value].Result.Outcome);
        Assert.Equal($"ejected from train {original.Value} reason=RedNewestMember " +
            "surviving-receipt=surviving-receipt", first.Reason);
        Assert.Equal(waiting, results[goal.Id.Value] with
        {
            Result = results[goal.Id.Value].Result with { Outcome = first with { Reason = Fixture.WaitingReason } }
        });
        // A different attempt readmits this goal; its receipt must replace the ejection explanation.
        var readmitted = new MergeTrainReceipt("readmitted-receipt",
            MergeTrainIdentity.Create(original.Members, original.ObservedMainRevision,
                new string('d', 40), original.ManifestIdentity),
            MergeTrainGateOutcome.Passed, DateTimeOffset.UnixEpoch.AddMinutes(3), 10, [], 0, [])
        { GateAttemptId = "readmitted-attempt" };
        MergeTrainDropBacktestTests.Fixture.Save(fixture.Store, fixture.Root, readmitted);

        ConductorBatchLoop.HoldCohortMembers([goal], results, fixture.Driver,
            ConductorAutonomyPolicy.Conservative, 8, lines);

        Assert.Equal(waiting, Assert.Single(results).Value);
        Assert.Equal("Acceptance capacity is occupied; retry next tick.",
            Assert.IsType<ConductorAdvanceOutcome.Held>(results[goal.Id.Value].Result.Outcome).Reason);
        Assert.Empty(lines);
    }

    private static ParallelLandingOutcome WaitingOutcome(Goal goal) => new(
        new ConductorAdvanceResult(goal.Id.Value, goal.Id.Value[..8], "Conservative",
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, Fixture.WaitingReason, "capacity")), 2);

    private static GateReadyCandidateProjection Projection(Goal goal) => new(goal.Id,
        GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior,
        ConductorTransitionDecision.Auto, [$"src/{goal.Id.Value}.cs"], [],
        new GateReadyMergeEvidence(new string('b', 40), new string('a', 40),
            GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));

    private sealed class Fixture : IDisposable
    {
        internal const string WaitingReason = "Acceptance capacity is occupied; retry next tick.";
        private readonly string _worktree = Path.Combine(Path.GetTempPath(), $"hold-pass-{Guid.NewGuid():N}");
        internal string Root { get; }
        internal string StorePath => Path.Combine(Root, "merge-train-acceptance.db");
        internal MergeTrainAcceptanceStore Store { get; }
        internal ConductorDriver Driver { get; }
        internal Goal[] Goals { get; }
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddMinutes(3);

        internal Fixture()
        {
            var workspace = OrchestratorWorkspace.ForDirectory(_worktree);
            Root = workspace.OrchestratorDirectory;
            InfrastructureTestSupport.SetUpStateDatabase(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            Goals = Enumerable.Range(1, 5).Select(index =>
                kernel.CreateGoal(new GoalId(index.ToString("x32")), $"Member {index}")).ToArray();
            Store = new MergeTrainAcceptanceStore(StorePath);
            Driver = new(kernel, workspace, new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
                    new AcceptanceVerificationResult(true, false, 0, null)),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                utcNow: () => Now);
        }

        internal MergeTrainIdentity SeedEjectedTrain()
        {
            var original = MergeTrainIdentity.Create(
                [MergeTrainDropBacktestTests.Fixture.Member(4), MergeTrainDropBacktestTests.Fixture.Member(6),
                 MergeTrainDropBacktestTests.Fixture.Member(5)], new string('a', 40), new string('c', 40), "manifest");
            MergeTrainDropBacktestTests.Fixture.Save(Store, Root, new MergeTrainReceipt("original-receipt",
                original, MergeTrainGateOutcome.Failed, DateTimeOffset.UnixEpoch.AddMinutes(1), 10, ["gate"], 1, [])
            { GateAttemptId = "red-then-retry" });
            Store.RecordEjections("red-then-retry",
                [new(Goals[4].Id, MergeTrainEjectionReason.RedNewestMember, [], "Dropped after RED receipt original-receipt.")]);
            MergeTrainDropBacktestTests.Fixture.Save(Store, Root, new MergeTrainReceipt("surviving-receipt",
                MergeTrainIdentity.Create(original.Members.Take(2).ToArray(), original.ObservedMainRevision,
                    new string('f', 40), original.ManifestIdentity),
                MergeTrainGateOutcome.Passed, DateTimeOffset.UnixEpoch.AddMinutes(2), 10, [], 0, [])
            { GateAttemptId = "red-then-retry" });
            Assert.False(Driver.TryGetCohortGateHold(Goals[4].Id, out _));
            return original;
        }

        public void Dispose() => Directory.Delete(_worktree, recursive: true);
    }
}
