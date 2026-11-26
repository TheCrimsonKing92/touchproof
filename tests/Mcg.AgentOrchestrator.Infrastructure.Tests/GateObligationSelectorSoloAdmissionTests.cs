using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its kernel, candidate evidence and build-slot storage.
public sealed class GateObligationSelectorSoloAdmissionTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const string Selector = "PlannedTests";
    private static readonly string Candidate = new('a', 40);
    private static readonly string Main = new('b', 40);
    private static readonly ConductorAutonomyPolicy Policy = ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = 1 };

    [Theory]
    [InlineData("PlannedTestsLauncherTests")]
    [InlineData("FamilyPlannedTests")]
    public void UniqueMatch_RebindsOnceBeforeGateStarts(string replacement)
    {
        using var fixture = new AdmissionFixture(Selector);
        fixture.Inventory = [$"Fixture.{replacement}"];

        var tick = fixture.Tick();

        var answer = Assert.Single(Assert.Single(fixture.AnswersAtGateStart));
        Assert.Equal($"{Selector} becomes {replacement}", answer);
        var request = Assert.Single(fixture.Kernel.HumanInputRequests);
        Assert.Equal(HumanInputAnswerOrigin.Operator, request.AuthoritativeAnswer!.Origin);
        Assert.Contains(Candidate, request.Question, StringComparison.Ordinal);
        Assert.Single(tick.ProgressLines!, line => line.Contains("GATE_TEST_OBLIGATION_PRE_ADMISSION_REBOUND",
            StringComparison.Ordinal));
        Assert.Single(fixture.Attempts);
        Assert.Equal(1, fixture.InventoryReads);
        Assert.Equal(2, fixture.RevisionReads);
        Assert.Empty(fixture.Feedback);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("A.PlannedTestsA", "Z.PlannedTestsB", "Ambiguous")]
    [InlineData("A.OtherTests", "Z.UnrelatedTests", "Unmatched")]
    public void UnresolvedSelector_HoldsAndRoutesOnce(string first, string second, string kind)
    {
        using var fixture = new AdmissionFixture(Selector);
        fixture.Inventory = [second, first];
        var reason = $"candidate {Candidate}; effective selector {Selector}; candidate classes considered: {first}, {second}";

        AssertHold(fixture.Tick(), reason);
        var feedback = Assert.Single(fixture.Feedback);
        Assert.Contains($"selector={Selector} candidate={Candidate} kind={kind}", feedback, StringComparison.Ordinal);
        Assert.Contains($"Candidate classes considered: {first}, {second}", feedback, StringComparison.Ordinal);

        AssertHold(fixture.Tick(), reason);
        Assert.Equal(feedback, Assert.Single(fixture.Feedback));
        Assert.Equal(2, fixture.InventoryReads);
        Assert.Empty(fixture.Attempts);
        Assert.Empty(fixture.AnswersAtGateStart);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
    }

    [Fact]
    public void ExactClass_AdmitsUnchangedAfterOneInventoryRead()
    {
        using var fixture = new AdmissionFixture(Selector);
        fixture.Inventory = [$"Fixture.{Selector}"];

        fixture.Tick();

        AssertUnchangedGateStart(fixture, expectedInventoryReads: 1);
    }

    [Theory]
    [InlineData("PlannedTests.Row", false)]
    [InlineData(Selector, true)]
    public void NonClassOrSatisfiedObligation_AdmitsWithoutInventory(string selector, bool satisfied)
    {
        using var fixture = new AdmissionFixture(selector);
        fixture.ProjectionAvailable = satisfied;
        if (satisfied)
        {
            ProspectiveEvidenceGateFixture.WriteAttempt(fixture.OrchestratorDirectory, fixture.Goal, Candidate,
                [($"{Selector}.Row", "Passed")]);
            var evaluation = Assert.Single(GateTestObligationEvaluator.Evaluate(
                fixture.Goal, fixture.OrchestratorDirectory, Candidate));
            Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, evaluation.Outcome);
        }

        fixture.Tick();

        AssertUnchangedGateStart(fixture, expectedInventoryReads: 0);
        Assert.Equal(0, fixture.RevisionReads);
    }

    [Theory]
    [InlineData("missing-projection", 0, 0)]
    [InlineData("empty-inventory", 1, 2)]
    [InlineData("unreadable-inventory", 1, 1)]
    [InlineData("changed-revision", 1, 2)]
    public void UnavailableCandidateInventory_HoldsBeforeGate(string scenario, int scans, int revisions)
    {
        using var fixture = new AdmissionFixture(Selector);
        fixture.ProjectionAvailable = scenario != "missing-projection";
        fixture.Inventory = scenario == "empty-inventory" ? [] : [$"Fixture.{Selector}LauncherTests"];
        fixture.InventoryUnreadable = scenario == "unreadable-inventory";
        fixture.RevisionAfterScan = scenario == "changed-revision" ? new string('c', 40) : Candidate;

        var tick = fixture.Tick();

        var expectedRevision = fixture.ProjectionAvailable ? Candidate : "none";
        AssertHold(tick, $"inventory unavailable at candidate {expectedRevision}; candidate classes considered: none");
        Assert.Equal(scans, fixture.InventoryReads);
        Assert.Equal(revisions, fixture.RevisionReads);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.Empty(fixture.Feedback);
        Assert.Empty(fixture.Attempts);
        Assert.Empty(fixture.AnswersAtGateStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InteractionPriorityObservation_LeavesAnswersAndFeedbackEmpty(bool uniqueMatch)
    {
        using var fixture = new AdmissionFixture(Selector);
        fixture.Inventory = uniqueMatch
            ? [$"Fixture.{Selector}LauncherTests"]
            : [$"A.{Selector}A", $"B.{Selector}B"];

        // The interaction-only priority selector calls this same observational evaluator.
        var admission = fixture.Observe();

        Assert.Equal(uniqueMatch ? ConductorBatchLoop.SoloAcceptanceAdmissionKind.Admissible
            : ConductorBatchLoop.SoloAcceptanceAdmissionKind.UnresolvedObligationSelector, admission.Kind);
        Assert.Equal(1, fixture.InventoryReads);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.Empty(fixture.Feedback);
        Assert.Empty(fixture.Attempts);
        Assert.Empty(fixture.AnswersAtGateStart);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void TransientRetryPriority_AdmitsVerifyingGoalWithoutProjectionOrInventory()
    {
        using var fixture = new AdmissionFixture();
        fixture.Tick();
        Assert.Equal(GoalStatus.Verifying, fixture.Goal.Status);
        var priorAttempt = Assert.Single(fixture.Attempts);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred, priorAttempt.Outcome);
        fixture.SeedSelectors(Selector);
        fixture.Inventory = [$"A.{Selector}A", $"B.{Selector}B"];
        var excluded = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(fixture.Project());
        Assert.Equal(GateReadyCandidateExclusionReason.LifecycleNotReady, excluded.Reason);

        var tick = fixture.Tick();

        Assert.Single(tick.ProgressLines!, line => line.Contains(
            $"reason=transient-retry-priority goal={fixture.Goal.Id.Value[..8]}", StringComparison.Ordinal));
        Assert.Equal(2, fixture.Attempts.Length);
        Assert.Equal(2, fixture.AnswersAtGateStart.Count);
        Assert.All(fixture.AnswersAtGateStart, answers => Assert.Empty(answers));
        Assert.Equal(0, fixture.InventoryReads);
        Assert.Equal(0, fixture.RevisionReads);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.Empty(fixture.Feedback);
    }

    private static void AssertHold(BatchTickSummary tick, string reason)
    {
        Assert.Equal(1, tick.Held);
        Assert.Contains(tick.ProgressLines!, line =>
            line.Contains($"selector-not-found: {Selector}; {reason}", StringComparison.Ordinal));
    }

    private static void AssertUnchangedGateStart(AdmissionFixture fixture, int expectedInventoryReads)
    {
        Assert.Empty(Assert.Single(fixture.AnswersAtGateStart));
        Assert.Single(fixture.Attempts);
        Assert.Equal(expectedInventoryReads, fixture.InventoryReads);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.Empty(fixture.Feedback);
    }

    private sealed class AdmissionFixture : IDisposable
    {
        private const string LandingPath = "src/Mcg.AgentOrchestrator.App/Orchestration/Fixture.cs";
        private readonly string _root = CreateTempDirectory("mcg-solo-selector");
        private readonly GoalId _goalId;
        private readonly ConductorDriver _driver;
        private readonly ConductorBatchLoop _loop;
        internal AgentOrchestratorKernel Kernel { get; private set; } = new();
        internal Goal Goal => Kernel.GetGoal(_goalId);
        internal IReadOnlyList<string> Feedback => Assert.Single(Goal.Tasks).CriterionRetryFeedback;
        internal string OrchestratorDirectory => Path.Combine(_root, ".orchestrator");
        internal ConductorParallelAcceptanceAttempt[] Attempts
        {
            get
            {
                var directory = Path.Combine(AttemptRoot, _goalId.Value);
                return !Directory.Exists(directory) ? [] : Directory.EnumerateFiles(directory, "*.attempt.json")
                    .Select(path => JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                        File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
            }
        }
        private string AttemptRoot => Path.Combine(_root, "solo-attempts");
        internal List<string[]> AnswersAtGateStart { get; } = [];
        internal string[] Inventory { get; set; } = [];
        internal bool ProjectionAvailable { get; set; } = true;
        internal bool InventoryUnreadable { get; set; }
        internal string RevisionAfterScan { get; set; } = Candidate;
        internal int InventoryReads { get; private set; }
        internal int RevisionReads { get; private set; }

        internal AdmissionFixture(params string[] selectors)
        {
            var goal = CreateVerifiedSimpleGoal(Kernel, "Verify solo selector admission");
            _goalId = goal.Id;
            Kernel.RecordGoalRefinement(_goalId,
                new RefinedSpec(goal.Objective, ["Worker checks output"], VerificationClass.TestVerifiable, [], []));
            if (selectors.Length > 0) SeedSelectors(selectors);
            _loop = new ConductorBatchLoop
            {
                SoloObligationOrchestratorDirectory = OrchestratorDirectory,
                SoloObligationInventorySource = new(
                    () =>
                    {
                        RevisionReads++;
                        return InventoryReads == 0 ? Candidate : RevisionAfterScan;
                    },
                    () =>
                    {
                        InventoryReads++;
                        if (InventoryUnreadable) throw new IOException("Injected inventory read failure.");
                        return Inventory.Select(name => new AcceptanceTestClassDescriptor(name, null)).ToArray();
                    })
            };
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(AttemptRoot,
                TrunkBranchName.Default, runInline: true, buildStorageRoot: new(Path.Combine(_root, "build")));
            var projector = new GateReadyCandidateProjector(
                _ => ProjectionAvailable ? new GateReadyCandidateRevisionPair(Candidate, Main) : null,
                _ => new GateReadyLandingScopeObservation(true, [LandingPath]),
                (_, _, _) => new GateReadyMergeTreeObservation(true));
            _driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                {
                    AnswersAtGateStart.Add(Kernel.HumanInputRequests
                        .Select(request => request.AuthoritativeAnswer!.Text).ToArray());
                    throw new AcceptanceInfrastructureDeferredException(
                        "trusted-main-build-failed", 1, "test transient deferral");
                },
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: _ => [LandingPath],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                resolveAcceptanceHeads: _ => (Candidate, Main),
                parallelAcceptanceAttemptCoordinator: coordinator,
                runMergeTrain: (_, _, _) => throw new InvalidOperationException("A single goal cannot form a merge train."));
        }

        internal void SeedSelectors(params string[] selectors) =>
            Kernel = GateTestObligationIntakeTests.Seed(Kernel, Goal, selectors);

        internal GateReadyCandidateProjectionResult Project() => _driver.ProjectCachedGateReadyCandidate(Goal, Policy);

        internal ConductorBatchLoop.SoloAcceptanceAdmission Observe() => _loop.EvaluateSoloAcceptanceAdmissibility(
            Kernel, _driver, Goal, Policy, new([]), 1, new HashSet<int>(), [], new HashSet<string>(), default,
            oldestServedThisTick: false, tick: 1);

        internal BatchTickSummary Tick()
        {
            using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
            BatchTickSummary? tick = null;
            _loop.Run(Kernel, _driver, Policy, NoStopPath(), maxIterations: 1, onTick: observed => tick = observed);
            return Assert.IsType<BatchTickSummary>(tick);
        }

        public void Dispose() => TryDeleteDirectory(_root);
    }
}
