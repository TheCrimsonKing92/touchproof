using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static GateObligationSelectorPreAdmissionTests;
using Admission = Mcg.AgentOrchestrator.App.Orchestration.ConductorBatchLoop.SoloAcceptanceAdmission;
using AdmissionKind = Mcg.AgentOrchestrator.App.Orchestration.ConductorBatchLoop.SoloAcceptanceAdmissionKind;
using SoloInventoryPass = Mcg.AgentOrchestrator.App.Orchestration.GateObligationSelectorPreAdmissionClassifier.SoloInventoryPass;

// Parallel-safe: each case owns its evidence and store. The live-tree scan is read-only.
[LiveTreeStructural]
public sealed class GateObligationSelectorSoloAdmissionWarningTests
{
    [Fact]
    public void ResolvedSelector_AdmitsWithoutWarning()
    {
        using var goal = new Case("PlannedTests");
        var store = new DeclaredTestInventoryRevisionStore();
        var lines = new List<string>();
        var pass = new SoloInventoryPass(store, lines.Add);

        var result = Evaluate(goal, pass, () => Inventory("Fixture.PlannedTests"));

        Assert.Equal(AdmissionKind.Admissible, result.Kind);
        Assert.Equal(1, store.Reads);
        Assert.Empty(lines);
    }

    [Theory]
    [InlineData("Fixture.OtherTests", "Fixture.UnrelatedTests")]
    [InlineData("Fixture.PlannedTestsOne", "Fixture.PlannedTestsTwo")]
    public void UnmatchedOrAmbiguousSelector_HoldsWithRebindGuidance(string first, string second)
    {
        using var goal = new Case("PlannedTests", "LaterTests");
        var lines = new List<string>();
        var pass = new SoloInventoryPass(new(), lines.Add);
        var inventory = Inventory([first, second, .. Enumerable.Range(1, 40).Select(index => $"Fixture.ExtraTests{index}")]);

        var result = Evaluate(goal, pass, () => inventory);

        var goalPrefix = goal.Goal.Id.Value[..8];
        var rebindCommand = $"obligation-rebind {goalPrefix} PlannedTests <DeclaredClass> --text-file <path>";
        Assert.Equal(AdmissionKind.UnresolvedObligationSelector, result.Kind);
        Assert.Contains("selector-not-found: PlannedTests", result.Reason);
        Assert.Contains(rebindCommand, result.Reason);
        var outcome = Assert.IsType<ParallelLandingOutcome>(ConductorBatchLoop.ApplySoloAcceptanceHold(
            result, null!, goal.Goal, ConductorAutonomyPolicy.Conservative, lines));
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(outcome.Result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal(result.Reason, held.Reason);
        Assert.InRange(held.Reason.Length, rebindCommand.Length, 512);
        Assert.Contains(ConductorDiagnosticText.SanitizeReason(rebindCommand),
            ConductorBatchLoop.FormatGoalProgressLine(goalPrefix, held));
        Assert.Empty(lines);
        Assert.Empty(goal.Kernel.HumanInputRequests);
    }

    [Theory]
    [InlineData("throw", "InvalidDataException: test inventory could not load")]
    [InlineData("null", "inventory reader returned no inventory")]
    [InlineData("empty", "declared inventory is empty")]
    public void UnavailableInventory_AdmitsWithOneWarning(string failure, string expectedCause)
    {
        using var goal = new Case("PlannedTests", "LaterTests");
        var lines = new List<string>();
        var pass = new SoloInventoryPass(new(), lines.Add);

        var result = Evaluate(goal, pass, () => failure switch
        {
            "throw" => throw new InvalidDataException("test inventory\ncould not load"),
            "null" => null,
            "empty" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        });

        Assert.Equal(AdmissionKind.Admissible, result.Kind);
        AssertWarning(goal, Assert.Single(lines), expectedCause);
    }

    [Fact]
    public void RepositoryInventory_ContainsSharedCollectionClassAndAdmits()
    {
        const string selector = nameof(AcceptanceGateEngineSettingsTests);
        using var goal = new Case(selector);
        var store = new DeclaredTestInventoryRevisionStore();
        var lines = new List<string>();
        var pass = new SoloInventoryPass(store, lines.Add);
        var root = VerifiedRepositoryRoot.Find();
        IReadOnlyList<AcceptanceTestClassDescriptor>? inventory = null;

        var result = Evaluate(goal, pass,
            () => inventory = GateObligationSelectorPreAdmissionClassifier.ReadDeclaredInventory(root));

        Assert.Equal(1, store.Reads);
        Assert.NotNull(inventory);
        Assert.NotEmpty(inventory);
        var test = Assert.Single(inventory, item => item.FullName == selector);
        Assert.Equal(TestCollections.GoalAcceptanceVerifier, test.Collection);
        Assert.False(test.CollectionUnknown);
        Assert.Equal(AdmissionKind.Admissible, result.Kind);
        Assert.Empty(lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoGoalsAtSameRevision_ShareOneInventoryScan(bool scanFails)
    {
        using var first = new Case("PlannedTests");
        using var second = new Case("PlannedTests");
        Assert.NotEqual(first.Goal.Id, second.Goal.Id);
        var store = new DeclaredTestInventoryRevisionStore();
        var lines = new List<string>();
        var pass = new SoloInventoryPass(store, lines.Add);
        var scans = 0;

        IReadOnlyList<AcceptanceTestClassDescriptor> Scan()
        {
            scans++;
            return scanFails
                ? throw new InvalidDataException("inventory read failed")
                : Inventory("Fixture.PlannedTests");
        }

        Assert.Equal(AdmissionKind.Admissible, Evaluate(first, pass, Scan).Kind);
        Assert.Equal(AdmissionKind.Admissible, Evaluate(second, pass, Scan).Kind);
        Assert.Equal(1, scans);
        Assert.Equal(1, store.Reads);
        if (scanFails)
        {
            Assert.Collection(lines,
                line => AssertWarning(first, line, "InvalidDataException: inventory read failed"),
                line => AssertWarning(second, line, "InvalidDataException: inventory read failed"));
        }
        else
        {
            Assert.Empty(lines);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedInventory_StillRequiresEachGoalWorktreeToBind(bool dirty)
    {
        using var first = new Case("PlannedTests");
        using var second = new Case("MissingTests");
        var store = new DeclaredTestInventoryRevisionStore();
        var lines = new List<string>();
        var pass = new SoloInventoryPass(store, lines.Add);
        const string dirtyCause = "worktree is not clean at the candidate";
        Assert.Equal(AdmissionKind.Admissible, Evaluate(first, pass, () => Inventory("Fixture.PlannedTests")).Kind);

        var result = Evaluate(second, pass, () => throw new InvalidOperationException("unexpected rescan"),
            () => dirty ? (null, dirtyCause) : (NextCandidate, null));

        Assert.Equal(AdmissionKind.Admissible, result.Kind);
        Assert.Equal(1, store.Reads);
        AssertWarning(second, Assert.Single(lines),
            dirty ? dirtyCause : $"candidate revision mismatch (worktree HEAD {NextCandidate})");
    }

    [Fact]
    public void PriorityProbe_WithoutSoloPassDoesNotReadInventory()
    {
        using var goal = new Case("MissingTests");
        var scans = 0;
        var bindings = 0;

        var result = Evaluate(goal, null, () =>
        {
            scans++;
            return Inventory("Fixture.OtherTests");
        }, () =>
        {
            bindings++;
            return (Candidate, null);
        });

        Assert.Equal(AdmissionKind.Admissible, result.Kind);
        Assert.Equal(0, scans);
        Assert.Equal(0, bindings);
    }

    private static Admission Evaluate(Case goal, SoloInventoryPass? pass,
        Func<IReadOnlyList<AcceptanceTestClassDescriptor>?> scan,
        Func<(string? Revision, string? UnavailableCause)>? readRevision = null) =>
        GateObligationSelectorPreAdmissionClassifier.CheckSoloWithStore(goal.Kernel, goal.Goal,
            goal.Evidence.OrchestratorDirectory, Candidate, null, pass,
            readRevision ?? (() => (Candidate, null)), scan,
            new(AdmissionKind.Admissible, ConductorParallelAcceptanceCandidate.Create(goal.Goal, 0, ["src/Fixture.cs"])));

    private static void AssertWarning(Case goal, string line, string cause)
    {
        Assert.Equal($"GATE_OBLIGATION_SELECTOR_PRE_ADMISSION_WARNING goal={goal.Goal.Id.Value[..8]} " +
            $"candidate={Candidate} result=admitted cause={cause}", line);
        Assert.DoesNotContain("selector-not-found", line);
    }
}
