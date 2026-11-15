using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorBatchLoop;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsMergeTrainOldestWaiterPassBoundTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FreeSlot_GoesToProtectedNormalHeadBeforeAnyTrain(int repetition)
    {
        using var fixture = new Fixture();
        fixture.Tick();
        Assert.Equal(GoalPriority.Normal, fixture.Goals[0].Priority);
        Assert.Empty(fixture.Trains);
        Assert.Equal(fixture.Goals[0].Id, fixture.Solos[0]);
        Assert.Contains(fixture.Lines, line => line.Contains("result=train-declined") &&
            line.Contains("oldest=" + fixture.Goals[0].Id.Value[..8]));
        _ = repetition; // Independent runs guard accidental dependence on static fairness history.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreedSlot_UsesSameRuleOnFirstTickAfterRelaunch(bool relaunch)
    {
        using var fixture = new Fixture();
        fixture.Occupied = true;
        fixture.Tick();
        Assert.Empty(fixture.Trains);
        Assert.Empty(fixture.Solos);
        fixture.Occupied = false;
        if (relaunch)
        {
            ResetParallelAcceptanceFairnessForTests();
            fixture.Loop = new();
        }
        fixture.Tick();
        Assert.Empty(fixture.Trains);
        Assert.Equal(fixture.Goals[0].Id, fixture.Solos[0]);
    }

    [Fact]
    public void TrainContainingNormalHead_IsAdmitted()
    {
        using var fixture = new Fixture(protectedHead: false);
        fixture.Tick();
        Assert.Equal(fixture.Goals.Select(goal => goal.Id), Assert.Single(fixture.Trains));
        Assert.Empty(fixture.Solos);
        Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=train-declined"));
    }

    [Fact]
    public void OccupiedSlot_LeavesTrainAndSoloAdmissionClosed()
    {
        using var fixture = new Fixture();
        fixture.Occupied = true;
        fixture.Tick();
        Assert.Empty(fixture.Trains);
        Assert.Empty(fixture.Solos);
        Assert.Contains(fixture.Lines, line => line.Contains("acceptance_width_1_reached"));
        Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=train-skipped") || line.Contains("result=train-declined"));
    }

    [Fact]
    public void SpareCapacity_AdmitsTrainAndProtectedHeadTogether()
    {
        using var fixture = new Fixture();
        fixture.Tick(width: 2);
        Assert.Equal(fixture.Goals.Skip(1).Select(goal => goal.Id), Assert.Single(fixture.Trains));
        Assert.Contains(fixture.Goals[0].Id, fixture.Solos);
        Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=train-declined"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifyingHeadWithoutAttempt_LeavesTrainPriorityUnchanged(bool trainsEnabled)
    {
        using var fixture = new Fixture(trainsEnabled: trainsEnabled);
        var head = fixture.Goals[0];
        Assert.True(fixture.Kernel.BeginGoalAcceptanceVerification(head.Id, "verification without an acceptance attempt"));
        Assert.Equal(GoalStatus.Verifying, head.Status);
        Assert.False(fixture.HasAttempt(head));
        Assert.Equal(trainsEnabled, fixture.Driver.MergeTrainsEnabled);

        fixture.Tick();

        if (trainsEnabled)
        {
            Assert.Equal(fixture.Goals.Skip(1).Select(goal => goal.Id), Assert.Single(fixture.Trains));
            Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=train-declined"));
        }
        else
        {
            Assert.Empty(fixture.Trains);
            Assert.Equal(head.Id, fixture.Solos[0]);
            Assert.True(fixture.HasAttempt(head));
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void RepeatedTicks_EmitSkipAndDeclineOnlyOncePerObservation()
    {
        using var fixture = new Fixture(extraProtectedGoal: true);
        for (var tick = 0; tick < 3; tick++)
        {
            var before = fixture.SlotReads;
            fixture.Tick();
            Assert.True(fixture.SlotReads > before, "Each tick must reevaluate acceptance admission.");
        }
        foreach (var goal in new[] { fixture.Goals[0], fixture.Goals[3] })
            Assert.Single(fixture.Lines, line => line.Contains("result=train-skipped") &&
                line.Contains("reason=OwnerProtectedPath") && line.Contains("goal=" + goal.Id.Value[..8]));
        Assert.Single(fixture.Lines, line => line.Contains("result=train-declined"));
        Assert.Empty(fixture.Trains);
        Assert.NotEmpty(fixture.Solos);
    }

    [Fact]
    public void PendingHeadFacts_AllowReadyPeersToTrain()
    {
        using var fixture = new Fixture();
        using var repository = new AdditiveConflictGitFixture(goalId: fixture.Goals[0].Id);
        foreach (var peer in fixture.Goals.Skip(1))
            AdditiveConflictGitFixture.Git(repository.Repository, "update-ref", $"refs/heads/{GoalWorktrees.BranchName(peer.Id)}", repository.OriginalHead);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new AdmissionFactsTickReader(repository.Repository, "main", (inputs, _) => inputs.Select(input =>
            AdmissionFactsTestData.Fact(input) with { Scope = new(true, fixture.Paths[input.GoalId]) }).ToArray());
        reader.Begin(fixture.Goals.Skip(1), _ => []);
        AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Peer admission facts did not finish.");
        reader.Derivation.BeforeDerive = (input, _) =>
        {
            if (input.GoalId != fixture.Goals[0].Id) return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "Pending head derivation was not released.");
        };
        fixture.Driver.AdmissionFacts.Reader = reader;
        try
        {
            reader.Begin(fixture.Goals, _ => []);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(60)), "Head derivation did not enter its pending state.");
            fixture.Tick();
            Assert.Equal(fixture.Goals.Skip(1).Select(goal => goal.Id), Assert.Single(fixture.Trains));
            Assert.DoesNotContain(fixture.Goals[0].Id, fixture.Solos);
            Assert.Contains(fixture.Lines, line => line.Contains("reason=admission-facts-pending goal=" + fixture.Goals[0].Id.Value[..8]));
            Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=train-declined"));
        }
        finally
        {
            release.Set();
            reader.Derivation.Stop();
            AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Pending head derivation did not stop.");
        }
    }

    [Theory]
    [InlineData((int)SoloAcceptanceAdmissionKind.WorkspaceProbeBackoff)]
    [InlineData((int)SoloAcceptanceAdmissionKind.ResourceConflict)]
    [InlineData((int)SoloAcceptanceAdmissionKind.CandidateUnavailable)]
    public void UnavailableSoloHead_DoesNotReserveSlot(int kind)
    {
        using var fixture = new Fixture();
        Assert.False(MergeTrainOldestWaiterPassBound.ShouldDecline(fixture.Goals[0],
            fixture.Goals.Skip(1).Select(goal => goal.Id).ToArray(), false, true, new([]), (_, _) => new((SoloAcceptanceAdmissionKind)kind)));
    }

    [Fact]
    public void HandledOrInflightHead_DoesNotProbeSoloAdmission()
    {
        using var fixture = new Fixture();
        var probes = 0;
        foreach (var state in new[] { (Handled: true, Eligible: true), (Handled: false, Eligible: false) })
            Assert.False(MergeTrainOldestWaiterPassBound.ShouldDecline(fixture.Goals[0],
                fixture.Goals.Skip(1).Select(goal => goal.Id).ToArray(), state.Handled, state.Eligible, new([]),
                (_, _) => { probes++; return new(SoloAcceptanceAdmissionKind.Admissible); }));
        Assert.Equal(0, probes);
    }

    [Fact]
    public void Recorder_EmitsChangesRecurrencesAndNewInstanceObservations()
    {
        var recorder = new MergeTrainOldestWaiterPassBound();
        var goal = new GoalId(new string('a', 32));
        var peer = new GoalId(new string('b', 32));
        var other = new GoalId(new string('c', 32));
        var lines = new List<string>();
        Dictionary<GoalId, ConductorMergeTrainSkipReason> skips = new() { [goal] = ConductorMergeTrainSkipReason.OwnerProtectedPath };
        recorder.RecordSkips(skips, 1, lines.Add);
        recorder.RecordSkips(skips, 2, lines.Add);
        Assert.Single(lines);
        skips[goal] = ConductorMergeTrainSkipReason.MainRevisionMismatch;
        recorder.RecordSkips(skips, 3, lines.Add);
        Assert.Equal(2, lines.Count);
        recorder.RecordSkips(new Dictionary<GoalId, ConductorMergeTrainSkipReason>(), 4, lines.Add);
        recorder.RecordSkips(skips, 5, lines.Add);
        Assert.Equal(3, lines.Count);
        recorder.RecordDecline(goal, [peer], 1, lines.Add);
        recorder.RecordDecline(goal, [peer], 2, lines.Add);
        Assert.Equal(4, lines.Count);
        recorder.RecordDecline(goal, [peer, other], 3, lines.Add);
        recorder.RecordDecline(other, [peer], 4, lines.Add);
        Assert.Equal(6, lines.Count);
        recorder.RecordDecline(null, [], 5, lines.Add);
        recorder.RecordDecline(other, [peer], 6, lines.Add);
        Assert.Equal(7, lines.Count);
        var relaunched = new MergeTrainOldestWaiterPassBound();
        relaunched.RecordSkips(skips, 1, lines.Add);
        relaunched.RecordDecline(other, [peer], 1, lines.Add);
        Assert.Equal(9, lines.Count);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly Goal[] Goals;
        internal readonly Dictionary<GoalId, IReadOnlyList<string>> Paths;
        internal readonly List<GoalId[]> Trains = [];
        internal readonly List<GoalId> Solos = [];
        internal readonly List<string> Lines = [];
        internal readonly ConductorDriver Driver;
        internal ConductorBatchLoop Loop = new();
        internal bool Occupied;
        internal int SlotReads;
        private readonly string _root = CreateTempDirectory("train-head-admission");
        private readonly IDisposable _isolated = ConductorBatchLoopTestsParallelAcceptance.IsolatedDotnetRootScope();
        private readonly IDisposable _census;

        internal Fixture(bool protectedHead = true, bool extraProtectedGoal = false, bool trainsEnabled = true)
        {
            ResetParallelAcceptanceFairnessForTests();
            _census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => Occupied
                ? [new(123456, "external-acceptance", 0, TimeSpan.Zero, RunClass: GateHeartbeatRunClass.Acceptance)] : []);
            Goals = Enumerable.Range(0, extraProtectedGoal ? 4 : 3).Select(index =>
            {
                var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(), $"Waiter {index}");
                PassVerificationAt(Kernel, goal, goal.Tasks.Single(), DateTimeOffset.Parse("2026-10-01T00:00:00Z").AddMinutes(index));
                return goal;
            }).ToArray();
            Paths = Goals.Select((goal, index) => (goal.Id, Paths: (IReadOnlyList<string>)
                ((index == 0 && protectedHead) || index == 3 ? ["config/acceptance-manifest.json"] :
                    [$"tests/{new[] { "Mcg.AgentOrchestrator.TestSupport", "Mcg.AgentOrchestrator.Core.Tests", "Mcg.AgentOrchestrator.Infrastructure.Tests" }[index]}/Waiter.cs"])))
                .ToDictionary(item => item.Id, item => item.Paths);
            const string main = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string Branch(GoalId id) => id.Value.PadRight(40, 'b');
            foreach (var goal in Goals)
            {
                Kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective, ["The full acceptance gate passes"],
                    VerificationClass.TestVerifiable, [], []) { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                Kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "test",
                    CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: Branch(goal.Id));
            }
            Driver = MakeDriver(getFacts: _ => new(WorkspaceExists: true), classifyRisk: _ => ChangeRiskTier.DocsOnly,
                isVerificationGateSatisfied: _ => true, getLandingFileScopes: goal => Paths[goal.Id],
                resolveAcceptanceHeads: goal => (Branch(goal.Id), main),
                getAcceptanceSlotCount: _ => { SlotReads++; return 2; },
                gateReadyCandidateProjector: new(id => new(Branch(id), main), id => new(true, Paths[id]), (_, _, _) => new(true)),
                parallelAcceptanceAttemptCoordinator: new(_root, TrunkBranchName.Default, runInline: true),
                runAcceptanceWithSlot: (goal, _) => { Solos.Add(goal.Id); return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
                mergeTrainsEnabled: trainsEnabled,
                runMergeTrain: (selection, _, policy) =>
                {
                    Trains.Add(selection.Members.Select(member => member.GoalId).ToArray());
                    return new(null, selection.Members.ToDictionary(member => member.GoalId.Value, member =>
                        new ConductorAdvanceResult(member.GoalId.Value, member.GoalId.Value[..8], policy.Name,
                            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "admitted by train"))), [], "outcome=passed");
                });
        }

        internal bool HasAttempt(Goal goal) => Directory.Exists(Path.Combine(_root, goal.Id.Value)) &&
            Directory.EnumerateFiles(Path.Combine(_root, goal.Id.Value), "*.attempt.json").Any();

        internal void Tick(int width = 1) => Loop.Run(Kernel, Driver,
            ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = width }, NoStopPath(), maxIterations: 1,
            onTick: tick => Lines.AddRange(tick.ProgressLines ?? []));

        public void Dispose()
        {
            _census.Dispose();
            ResetParallelAcceptanceFairnessForTests();
            _isolated.Dispose();
            TryDeleteDirectory(_root);
        }
    }
}
