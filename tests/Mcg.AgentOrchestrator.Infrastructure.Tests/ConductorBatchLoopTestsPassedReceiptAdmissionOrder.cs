using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// The live census probe is scoped to this collection; no real gate process is launched.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsPassedReceiptAdmissionOrder : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsPassedReceiptAdmissionOrder(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void ValidPassedReceiptAtFullWidthLandsWithoutCapacityHold()
    {
        using var scene = new Scene();
        var tick = scene.Advance();

        scene.AssertNoCapacityHold(tick);
        Assert.Equal(1, scene.Landings);
        Assert.NotNull(scene.Receipt.ReplayLandedAt);
        Assert.Contains(tick.ProgressLines!, line => line.Contains("result=replayed", StringComparison.Ordinal));
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ActiveLandingHoldAtFullWidthPreservesReceiptWithoutCapacityHold(bool apparatus)
    {
        using var scene = new Scene();
        scene.HoldLanding(apparatus);
        var receipt = scene.Receipt;
        Assert.Equal(receipt.ReplayContextFingerprint,
            ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(scene.Goal));
        // This is the rejecting guard on the original code: candidate-none discards
        // hold-active, replay returns null, and solo admission reports acceptance width.
        Assert.Null(scene.Driver.TryBuildParallelAcceptanceCandidate(scene.Goal,
            ConductorAutonomyPolicy.Conservative, 0, out var decline));
        Assert.Equal(ConductorDriver.CandidateDeclineHoldActive, decline);

        var tick = scene.Advance();

        scene.AssertNoCapacityHold(tick);
        Assert.Contains(tick.ProgressLines!, line =>
            scene.IsGoalHold(line) && line.Contains(
                apparatus ? "apparatus" : "Owner_approval_required", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, scene.Landings);
        Assert.Equal(JsonSerializer.Serialize(receipt), JsonSerializer.Serialize(scene.Receipt));
        scene.Kernel.ClearAcceptanceFailure(scene.Goal.Id);
        scene.Advance();
        Assert.Equal(1, scene.Landings);
    }

    [Xunit.Fact]
    public void OlderCandidateReceiptAtFullWidthReceivesAcceptanceWidthHold()
    {
        using var scene = new Scene();
        scene.CurrentCandidate = new string('c', 40);
        scene.AssertCapacityHold(scene.Advance());
    }

    [Xunit.Fact]
    public void MainAdvancedWithOverlapAtFullWidthReceivesAcceptanceWidthHold()
    {
        using var scene = new Scene(native: true);
        scene.AdvanceMain("Directory.Build.props");
        scene.AssertCapacityHold(scene.Advance());
        Assert.Contains(GoalOperationJournal.Read(scene.Root, scene.Goal.Id).Entries, entry =>
            entry.Operation == "conductor:acceptance-main-advance" &&
            entry.Detail?.Contains("disposition=overlap", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Xunit.Fact]
    public void MainAdvancedDisjointAtFullWidthLandsWithoutCapacityHold()
    {
        using var scene = new Scene(native: true);
        scene.AdvanceMain("src/unrelated.cs");
        scene.AssertNoCapacityHold(scene.Advance());
        Assert.Equal(1, scene.Landings);
        Assert.NotNull(scene.Receipt.ReplayLandedAt);
    }

    [Xunit.Fact]
    public void NoPassedAttemptAtFullWidthReceivesAcceptanceWidthHold()
    {
        using var scene = new Scene(seedReceipt: false);
        scene.AssertCapacityHold(scene.Advance());
    }

    [Xunit.Fact]
    public void PendingGateEvidenceWithLandingHoldStillRequiresCapacity()
    {
        using var scene = new Scene();
        scene.Kernel.MapCriterionEvidenceOwner(scene.Goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "fixture prior binding", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: new string('d', 40));
        scene.Kernel.MapCriterionEvidenceOwner(scene.Goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "fixture rebind", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: scene.Candidate);
        scene.HoldLanding(apparatus: false);
        Assert.NotEmpty(scene.Goal.GetOutstandingCriterionEvidenceObligations(scene.Candidate));

        scene.AssertCapacityHold(scene.Advance());

        Assert.NotEmpty(scene.Goal.GetOutstandingCriterionEvidenceObligations(scene.Candidate));
    }

    [Xunit.Theory]
    [Xunit.InlineData("criteria")]
    [Xunit.InlineData("candidate")]
    [Xunit.InlineData("artifact")]
    [Xunit.InlineData("task")]
    [Xunit.InlineData("scope")]
    public void InvalidReceiptWithLandingHoldStillRequiresCapacity(string change)
    {
        using var scene = new Scene();
        switch (change)
        {
            case "criteria":
                scene.Kernel.RecordGoalRefinement(scene.Goal.Id, scene.Goal.RefinedSpec! with
                    { AcceptanceCriteria = ["Changed required behavior"] });
                break;
            case "candidate": scene.CurrentCandidate = new string('c', 40); break;
            case "artifact": File.AppendAllText(scene.Receipt.ResultPath, " "); break;
            case "task":
                scene.Kernel.RecordTaskVerification(scene.Goal.Id, scene.Goal.Tasks.Single().Id,
                    ManualVerificationRecorder.Create(true, "new verification", "fixture", DateTimeOffset.UtcNow));
                break;
            case "scope": scene.Scopes = ["src/changed.cs"]; break;
        }
        scene.HoldLanding(apparatus: false);
        scene.AssertCapacityHold(scene.Advance());
    }

    private sealed class Scene : IDisposable
    {
        public string Root { get; }
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public ConductorDriver Driver { get; }
        public string Candidate { get; }
        public string Main { get; }
        public string CurrentCandidate { get; set; }
        public string CurrentMain { get; private set; }
        public IReadOnlyList<string> Scopes { get; set; } = ["src/receipt.txt"];
        public int Landings { get; private set; }
        private int _gates;
        private int _workers;
        private readonly IDisposable _census;
        private readonly ConductorParallelAcceptanceAttemptCoordinator _coordinator;
        private readonly ConductorParallelAcceptanceAttempt _occupant;
        private readonly string? _receiptPath;
        public ConductorParallelAcceptanceAttempt Receipt => JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(_receiptPath!), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        public Scene(bool native = false, bool seedReceipt = true)
        {
            Root = native ? LandingExecutorTests.CreateGitRepository() : CreateTempDirectory("mcg-receipt-admission");
            if (native)
            {
                var seeded = LandingExecutorTests.CreateAcceptedCandidate(Root, "src/receipt.txt");
                Kernel = seeded.Kernel;
                Goal = seeded.Goal;
                Candidate = LandingExecutorTests.ReadGit(Root, "rev-parse", GoalWorktrees.BranchName(Goal.Id));
                Main = LandingExecutorTests.ReadGit(Root, "rev-parse", "main");
            }
            else
            {
                Kernel = new AgentOrchestratorKernel();
                Goal = CreateVerifiedSimpleGoal(Kernel, "Passed receipt admission ordering");
                Candidate = new string('a', 40);
                Main = new string('b', 40);
            }
            CurrentCandidate = Candidate;
            CurrentMain = Main;
            Kernel.RecordGoalRefinement(Goal.Id, new RefinedSpec(Goal.Objective,
                ["Required behavior"], VerificationClass.TestVerifiable, [], []));
            var obligation = Kernel.MapCriterionEvidenceOwner(Goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                "fixture", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: Candidate);
            Kernel.RecordCriterionEvidence(Goal.Id, obligation.Id, obligation.Owner, Candidate,
                "passing-gate", obligation.RequiredScope, true, "retained passed evidence");
            var attempts = Path.Combine(Root, ".orchestrator", "acceptance-gate-attempts");
            _coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attempts, TrunkBranchName.Default,
                runInline: true, isProcessAlive: _ => true, acquireStableSlotLease: (_, _) => null);
            if (seedReceipt)
            {
                var seed = _coordinator.Evaluate(ConductorParallelAcceptanceCandidate.Create(Goal, 0, Scopes, Candidate, Main),
                    ConductorAutonomyPolicy.Conservative, (candidate, _, _, _, _) =>
                        ConductorParallelAcceptanceRunResult.Accepted(candidate, new AcceptanceVerificationSummary(true, [],
                            BranchHeadSha: Candidate, MainHeadSha: Main)));
                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, seed.Kind);
                _coordinator.MarkReconciled(seed.Attempt);
                _receiptPath = seed.Attempt.MetadataPath;
            }
            _census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
            var otherGoal = CreateVerifiedSimpleGoal(Kernel, "Unrelated live gate");
            var other = new ConductorParallelAcceptanceAttemptCoordinator(attempts, TrunkBranchName.Default,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(9876));
            var started = other.Evaluate(ConductorParallelAcceptanceCandidate.Create(otherGoal, 0,
                ["src/occupant.cs"], Candidate, Main), ConductorAutonomyPolicy.Conservative,
                (_, _, _, _, _) => throw new InvalidOperationException("Occupant must stay live."));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
            _occupant = started.Attempt;
            Driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: _ => { _workers++; return DispatchStartOutcome.Started(); },
                runAcceptanceWithSlot: (_, _) => { _gates++; throw new InvalidOperationException("No gate expected."); },
                rebaseOntoMain: goal =>
                {
                    if (!native) return DefaultRebaseSuccess();
                    var result = GoalWorktrees.TryRebaseOntoMain(Root, goal.Id);
                    CurrentCandidate = LandingExecutorTests.ReadGit(Root, "rev-parse", GoalWorktrees.BranchName(goal.Id));
                    return result;
                },
                land: goal =>
                {
                    Landings++;
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(),
                        "integration", true, "Landed");
                }, getLandingFileScopes: _ => Scopes, parallelAcceptanceAttemptCoordinator: _coordinator,
                resolveAcceptanceHeads: _ => (CurrentCandidate, CurrentMain), executionDirectory: Root);
            Driver.OverrideOwnerReviewHoldForTests(
                CollaborationItemStore.ForDirectory(Path.Combine(Root, ".orchestrator")),
                (_, _) => { }, (_, _) => "review", Kernel);
        }

        public void HoldLanding(bool apparatus)
        {
            var check = apparatus ? "acceptance apparatus" : "owner-protected configuration";
            Kernel.RecordAcceptanceFailure(Goal.Id, [check], CurrentCandidate, CurrentMain,
                checkAttributions: apparatus ? [new AcceptanceCheckAttribution(check,
                    AcceptanceFailureOrigin.Unattributed, "fixture apparatus failure", AcceptanceFailureCause.EnvironmentalApparatus)] : null);
            if (!apparatus)
                GoalOperationJournal.OwnerReviewHoldEntered(Root, Goal, CurrentCandidate, CurrentMain,
                    new OwnerReviewHoldReceipt("review", Goal.LatestAcceptanceFailure!.OccurredAt, "Owner approval required"),
                    ["owner-protected configuration"]);
        }

        public void AdvanceMain(string path)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "main advanced");
            InfrastructureTestSupport.RunGitWithCommitPostcondition(Root, ["add", path]);
            InfrastructureTestSupport.RunGitWithCommitPostcondition(Root, ["commit", "-m", "Advance main"]);
            CurrentMain = LandingExecutorTests.ReadGit(Root, "rev-parse", "main");
            Assert.NotEqual(Main, CurrentMain);
        }

        public BatchTickSummary Advance()
        {
            Assert.Single(_coordinator.GetCapacityReservingAttempts([_occupant.GoalId]));
            BatchTickSummary? tick = null;
            new ConductorBatchLoop().Run(Kernel, Driver,
                ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 }, NoStopPath(),
                maxIterations: 1, onlyGoalId: Goal.Id.Value, onTick: current => tick = current);
            Assert.NotNull(tick);
            Assert.Equal(0, _gates);
            Assert.Equal(0, _workers);
            var occupant = Assert.Single(_coordinator.GetCapacityReservingAttempts([_occupant.GoalId]));
            Assert.Equal(_occupant.AttemptId, occupant.AttemptId);
            Assert.Equal(_occupant.Outcome, occupant.Outcome);
            Assert.Null(occupant.ReconciledAt);
            return tick;
        }

        // Goal progress emits reasons as tokens: spaces are replaced by underscores.
        // Check the emitted form in both arms so absence cannot pass vacuously.
        public void AssertNoCapacityHold(BatchTickSummary tick) =>
            Assert.DoesNotContain(tick.ProgressLines!, line => line.Contains("acceptance_width", StringComparison.Ordinal));

        public bool IsGoalHold(string line) =>
            line.StartsWith($"GOAL goal={Goal.Id.Value[..8]} result=held ", StringComparison.Ordinal);

        public void AssertCapacityHold(BatchTickSummary tick)
        {
            Assert.Contains(tick.ProgressLines!, line => IsGoalHold(line) &&
                line.Contains("reason=acceptance_width_1_reached;", StringComparison.Ordinal));
            Assert.Equal(0, Landings);
            if (_receiptPath is not null) Assert.Null(Receipt.ReplayLandedAt);
        }

        public void Dispose()
        {
            _census.Dispose();
            TryDeleteDirectory(Root);
        }
    }
}
