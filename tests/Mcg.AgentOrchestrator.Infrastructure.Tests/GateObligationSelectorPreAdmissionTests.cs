using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: kernels and evidence files are fixture-owned; revision and inventory I/O is injected.
public sealed class GateObligationSelectorPreAdmissionTests
{
    internal const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string NextCandidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("PlannedTests", "Fixture.PlannedTests", "Resolved")]
    [InlineData("PlannedTests", "Fixture.PlannedTestsLauncherTests", "UniquelyMatched")]
    [InlineData("PlannedTests", "Fixture.FamilyPlannedTests", "UniquelyMatched")]
    [InlineData("PlannedTests", "Fixture.FamilyPlannedTestsLauncher", "UniquelyMatched")]
    [InlineData("PlannedTests", "Fixture.plannedTestsLauncher", "Unmatched")]
    [InlineData("PlannedTests", "PlannedTests.OtherTests", "Unmatched")]
    public void Classify_UsesOrdinalSimpleNames(string selector, string name, string kind)
    {
        var result = Assert.Single(GateObligationSelectorPreAdmissionClassifier.Classify([selector], Inventory(name)));
        Assert.Equal(kind, result.Kind.ToString());
        Assert.Equal(selector, result.Selector);
    }

    [Fact]
    public void Classify_ExactNameWinsAndDistinctIdentitiesDetermineUniqueness()
    {
        var exact = Assert.Single(GateObligationSelectorPreAdmissionClassifier.Classify(["PlannedTests"],
            Inventory("Z.PlannedTestsLauncher", "A.PlannedTests", "B.PlannedTests")));
        Assert.Equal(GateObligationSelectorPreAdmissionClassifier.Kind.Resolved, exact.Kind);
        var duplicate = Assert.Single(GateObligationSelectorPreAdmissionClassifier.Classify(["PlannedTests"],
            Inventory("A.PlannedTestsLauncher", "A.PlannedTestsLauncher")));
        Assert.Equal(GateObligationSelectorPreAdmissionClassifier.Kind.UniquelyMatched, duplicate.Kind);
        var collision = Assert.Single(GateObligationSelectorPreAdmissionClassifier.Classify(["PlannedTests"],
            Inventory("Z.PlannedTestsLauncher", "A.PlannedTestsLauncher")));
        Assert.Equal(GateObligationSelectorPreAdmissionClassifier.Kind.Ambiguous, collision.Kind);
        Assert.Equal(new[] { "A.PlannedTestsLauncher", "Z.PlannedTestsLauncher" }, collision.CandidateFullNames);
    }

    [Fact]
    public void Classify_LeavesQualifiedMethodAndMalformedFormsOnExistingPaths()
    {
        Assert.Empty(GateObligationSelectorPreAdmissionClassifier.Classify(
            ["Fixture.PlannedTests", "PlannedTests.Row", "Outer+PlannedTests", "Row()", "two words", "check:x"],
            Inventory("PlannedTests")));
    }

    [Fact]
    public void UniqueMatch_RecordsOnceBeforeGateAndEvaluatorFollowsAnswer()
    {
        using var fixture = new Case("PlannedTests");
        var lines = new List<string>();
        var result = fixture.Apply(Inventory("Fixture.PlannedTestsLauncherTests"), lines.Add);

        Assert.False(result.Hold);
        var request = Assert.Single(fixture.Kernel.HumanInputRequests);
        var answer = request.AuthoritativeAnswer!;
        Assert.Equal("PlannedTests becomes PlannedTestsLauncherTests", answer.Text);
        Assert.Equal(HumanInputAnswerOrigin.Operator, answer.Origin);
        Assert.False(answer.IsAutoDefaulted);
        Assert.Equal(fixture.Goal.AuthoritativeBrief.Version, answer.BriefVersion);
        Assert.Contains("PlannedTests", request.Question);
        Assert.Contains("PlannedTestsLauncherTests", request.Question);
        Assert.Contains(Candidate, request.Question);
        Assert.Equal($"GATE_TEST_OBLIGATION_PRE_ADMISSION_REBOUND id=obligati old=PlannedTests " +
            $"new=PlannedTestsLauncherTests candidate={Candidate} request={request.Id.Value[..8]}", Assert.Single(lines));
        Assert.Equal(new[] { "PlannedTests" }, Assert.Single(fixture.Goal.GateTestObligations).Selectors);

        Assert.False(fixture.Apply(Inventory("Fixture.PlannedTestsLauncherTests"), lines.Add).Hold);
        Assert.Single(fixture.Kernel.HumanInputRequests);
        Assert.Single(lines);
        Assert.Empty(fixture.Developer.CriterionRetryFeedback);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        var (_, trxPath, _) = fixture.Evidence.WriteAttempt([("Fixture.PlannedTestsLauncherTests.Check", "Passed")]);
        // Supply the definition a real TRX uses to resolve a namespaced row by simple class name.
        var trx = XDocument.Load(trxPath);
        var ns = trx.Root!.Name.Namespace;
        Assert.Single(trx.Descendants(ns + "UnitTestResult")).SetAttributeValue("testId", "rebound-test");
        trx.Root.Add(new XElement(ns + "TestDefinitions",
            new XElement(ns + "UnitTest", new XAttribute("id", "rebound-test"),
                new XElement(ns + "TestMethod", new XAttribute("className", "Fixture.PlannedTestsLauncherTests"),
                    new XAttribute("name", "Check")))));
        trx.Save(trxPath);
        var baseline = Assert.Single(GateTestObligationEvaluator.Evaluate(fixture.Goal,
            fixture.Evidence.OrchestratorDirectory, Candidate));
        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, baseline.Outcome);
        var evaluation = Assert.Single(GateTestObligationEvaluator.Evaluate(fixture.Goal,
            fixture.Evidence.OrchestratorDirectory, Candidate, answeredRequests: fixture.Kernel.HumanInputRequests));
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, evaluation.Outcome);
        Assert.Equal(request.Id.Value, Assert.Single(evaluation.AnswerRebinds!).RequestId);
    }

    [Fact]
    public void ExactName_RecordsNothingAndAddsNoAdmissionRestriction()
    {
        using var fixture = new Case("PlannedTests");
        Assert.False(fixture.Apply(Inventory("Fixture.PlannedTests")).Hold);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.Empty(fixture.Developer.CriterionRetryFeedback);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Empty(ConductorBatchLoop.TrainIneligibleCriterionEvidenceGoalIds([fixture.Goal]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnresolvedSelector_ReportsConsideredClassesWithoutRecording(bool ambiguous)
    {
        using var fixture = new Case("PlannedTests");
        var names = ambiguous
            ? new[] { "Z.PlannedTestsB", "A.PlannedTestsA", "OtherTests" }
            : new[] { "Z.OtherTests", "A.OtherTests" };
        var result = fixture.Apply(Inventory(names));

        Assert.True(result.Hold);
        // The hold emits no rebind answer.
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.Equal($"gate test obligation obligati (brief v1) selector-not-found: PlannedTests; " +
            $"candidate {Candidate}; effective selector PlannedTests; candidate classes considered: " +
            (ambiguous ? "A.PlannedTestsA, Z.PlannedTestsB" : "A.OtherTests, Z.OtherTests"), Assert.Single(result.HoldLines));
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("unmatched")]
    [InlineData("unavailable")]
    public void Admission_UnresolvedGoalIsExcludedWhileResolvedGoalsFormTrain(string scenario)
    {
        using var held = new Case("PlannedTests");
        using var first = new Case("FirstTests");
        using var second = new Case("SecondTests");
        var inventory = scenario switch
        {
            "ambiguous" => Inventory("A.PlannedTestsA", "B.PlannedTestsB"),
            "unmatched" => Inventory("OtherTests"),
            _ => null
        };
        var heldResult = held.Apply(inventory);
        var firstResult = first.Apply(Inventory("FirstTests"));
        var secondResult = second.Apply(Inventory("SecondTests"));
        Assert.True(heldResult.Hold);
        Assert.False(firstResult.Hold);
        Assert.False(secondResult.Hold);
        if (scenario == "unavailable")
        {
            Assert.Empty(held.Developer.CriterionRetryFeedback);
        }
        else
        {
            var feedback = Assert.Single(held.Developer.CriterionRetryFeedback);
            Assert.Contains("selector=PlannedTests", feedback, StringComparison.Ordinal);
            Assert.Equal(1, held.Goal.AutomaticAcceptanceRetryCount);
        }
        Assert.Empty(first.Developer.CriterionRetryFeedback);
        Assert.Empty(second.Developer.CriterionRetryFeedback);
        Goal[] goals = [held.Goal, first.Goal, second.Goal];
        var classifications = new[] { (held.Goal, heldResult), (first.Goal, firstResult), (second.Goal, secondResult) };
        var excluded = ConductorBatchLoop.TrainIneligibleCriterionEvidenceGoalIds(goals,
            classifications.Where(item => item.Item2.Hold).Select(item => item.Goal.Id.Value).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(new[] { held.Goal.Id.Value }, excluded);
        var candidates = goals.Select(Ready).ToArray();
        var unrestricted = Assert.IsType<ConductorMergeTrainSelection>(ConductorMergeTrainSelector.Select(candidates));
        Assert.Equal(goals.Select(goal => goal.Id), unrestricted.Members.Select(member => member.GoalId));
        var skipped = new List<(GoalId GoalId, ConductorMergeTrainSkipReason Reason)>();
        var selection = Assert.IsType<ConductorMergeTrainSelection>(ConductorMergeTrainSelector.Select(
            candidates, ineligibleGoalIds: excluded, onSkipped: (id, reason) => skipped.Add((id, reason))));
        Assert.Equal((held.Goal.Id, ConductorMergeTrainSkipReason.IneligibleCriterionEvidence), Assert.Single(skipped));
        Assert.Equal(new[] { first.Goal.Id, second.Goal.Id }, selection.Members.Select(member => member.GoalId));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("throws")]
    public void InventoryUnavailable_HoldsWithoutAnswer(string scenario)
    {
        using var fixture = new Case("PlannedTests");
        var calls = 0;
        var result = GateObligationSelectorPreAdmissionClassifier.Apply(fixture.Kernel, fixture.Goal,
            fixture.Evidence.OrchestratorDirectory, Candidate, () =>
            {
                calls++;
                return scenario switch
                {
                    "null" => null,
                    "empty" => [],
                    _ => throw new IOException("inventory read failed")
                };
            }, _ => Assert.Fail("Unavailable inventory must not record a rebind"));
        Assert.Equal(1, calls);
        Assert.True(result.Hold);
        Assert.Contains(Candidate, Assert.Single(result.HoldLines));
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.Empty(fixture.Developer.CriterionRetryFeedback);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
    }

    [Fact]
    public void Admission_MissingWorkspaceHoldsGoalAndEmitsObligationLine()
    {
        using var fixture = new Case("PlannedTests");
        var lines = new List<string>();
        var held = GateObligationSelectorPreAdmissionClassifier.HeldGoalIds(fixture.Kernel, [fixture.Goal],
            [Ready(fixture.Goal)], null, lines.Add);
        Assert.Equal(new[] { fixture.Goal.Id.Value }, held);
        var line = Assert.Single(lines);
        Assert.Contains("selector-not-found: PlannedTests", line);
        Assert.Contains($"inventory unavailable at candidate {Candidate}", line);
        Assert.Contains(fixture.Goal.Id.Value,
            ConductorBatchLoop.TrainIneligibleCriterionEvidenceGoalIds([fixture.Goal], held));
        Assert.Empty(fixture.Kernel.HumanInputRequests);
    }

    [Fact]
    public void MixedSelectors_RecordUniqueAnswerEvenWhenSiblingHolds()
    {
        using var fixture = new Case("PlannedTests", "MissingTests");
        var result = fixture.Apply(Inventory("PlannedTestsLauncher"));
        Assert.True(result.Hold);
        Assert.Contains("MissingTests", Assert.Single(result.HoldLines));
        Assert.Equal("PlannedTests becomes PlannedTestsLauncher",
            Assert.Single(fixture.Kernel.HumanInputRequests).Answer);
    }

    [Theory]
    [InlineData("PlannedTests.Row", false)]
    [InlineData("PlannedTests", true)]
    public void NonClassOrSatisfiedObligation_DoesNotReadInventory(string selector, bool satisfied)
    {
        using var fixture = new Case(selector);
        if (satisfied) fixture.Evidence.WriteAttempt([("PlannedTests.Row", "Passed")]);
        var reads = 0;
        var result = GateObligationSelectorPreAdmissionClassifier.Apply(fixture.Kernel, fixture.Goal,
            fixture.Evidence.OrchestratorDirectory, Candidate, () => { reads++; return null; }, _ => { });
        Assert.False(result.Hold);
        Assert.Equal(0, reads);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
    }

    [Fact]
    public void HistoricalAndOtherRepositoryObligations_DoNotReadInventory()
    {
        using var fixture = new Case("PlannedTests");
        fixture.Kernel.ReviseGoalBrief(fixture.Goal.Id, "Current brief");
        var snapshot = fixture.Kernel.ExportSnapshot();
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                GateTestObligations = [
                    new("old", 1, ["HistoricalTests"], null, "Prior brief", DateTimeOffset.UnixEpoch),
                    new("other-repo", fixture.Goal.AuthoritativeBrief.Version, ["OtherTests"], null,
                        "Other repo", DateTimeOffset.UnixEpoch, RepositoryKey: "other")]
            }).ToArray()
        });
        var reads = 0;
        var result = GateObligationSelectorPreAdmissionClassifier.Apply(kernel, kernel.GetGoal(fixture.Goal.Id),
            null, Candidate, () => { reads++; return null; }, _ => { });
        Assert.False(result.Hold);
        Assert.Equal(0, reads);
        Assert.Empty(kernel.HumanInputRequests);
    }

    [Fact]
    public void MultiplePendingSelectors_ShareOneInventoryRead()
    {
        using var fixture = new Case("FirstTests", "SecondTests");
        var reads = 0;
        var result = GateObligationSelectorPreAdmissionClassifier.Apply(fixture.Kernel, fixture.Goal,
            fixture.Evidence.OrchestratorDirectory, Candidate,
            () => { reads++; return Inventory("FirstTests", "SecondTests"); }, _ => { });
        Assert.False(result.Hold);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidPairOrRecordFailure_HoldsWithoutProvenance(bool terminal)
    {
        using var fixture = new Case("PlannedTests");
        if (terminal) fixture.Kernel.CancelGoal(fixture.Goal.Id, "Test record rejection");
        var lines = new List<string>();
        var result = fixture.Apply(Inventory(terminal ? "PlannedTestsLauncher" : "lowerPlannedTests"), lines.Add);
        Assert.True(result.Hold);
        Assert.Empty(lines);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.Contains(terminal ? "InvalidOperationException" : "ArgumentException", Assert.Single(result.HoldLines));
    }

    [Fact]
    public void ConflictingRecordedAnswers_HoldWithoutAppendingEquivalentAnswer()
    {
        using var fixture = new Case("PlannedTests");
        fixture.Kernel.RecordAnsweredOperatorHumanInput(fixture.Goal.Id, "first", "PlannedTests becomes PlannedTestsLauncher");
        fixture.Kernel.RecordAnsweredOperatorHumanInput(fixture.Goal.Id, "second", "PlannedTests becomes OtherTests");
        Assert.True(fixture.Apply(Inventory("PlannedTestsLauncher")).Hold);
        Assert.Equal(2, fixture.Kernel.HumanInputRequests.Count);
    }

    [Fact]
    public void CandidateChanges_RefreshInventoryAndNeverFollowARebindChain()
    {
        using var fixture = new Case("PlannedTests");
        Assert.False(fixture.Apply(Inventory("PlannedTestsLauncher")).Hold);
        var result = GateObligationSelectorPreAdmissionClassifier.Apply(fixture.Kernel, fixture.Goal,
            fixture.Evidence.OrchestratorDirectory, NextCandidate,
            () => Inventory("PlannedTestsLauncherNext"), _ => Assert.Fail("Do not record a rebind chain"));
        Assert.True(result.Hold);
        Assert.Contains(NextCandidate, Assert.Single(result.HoldLines));
        Assert.Single(fixture.Kernel.HumanInputRequests);
        Assert.False(fixture.Apply(Inventory("PlannedTestsLauncher")).Hold);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateInventory_RejectsWrongOrChangingRevision(bool changesDuringScan)
    {
        var reads = 0;
        var scans = 0;
        var result = GateObligationSelectorPreAdmissionClassifier.ReadCandidateInventory(Candidate,
            () => ++reads == 1 && changesDuringScan ? Candidate : NextCandidate,
            () => { scans++; return Inventory("PlannedTestsLauncher"); });
        Assert.Null(result);
        Assert.Equal(changesDuringScan ? 1 : 0, scans);
        Assert.Equal(changesDuringScan ? 2 : 1, reads);
        Assert.Single(GateObligationSelectorPreAdmissionClassifier.ReadCandidateInventory(Candidate,
            () => Candidate, () => Inventory("PlannedTestsLauncher"))!);
    }

    [Theory]
    [InlineData("CandidateIdentityTests")]
    [InlineData("AcceptanceCriteriaParserTests")]
    public void Admission_ClassOutsideLaneRoots_ResolvesWithoutAnswerOrHold(string selector)
    {
        using var fixture = new Case(selector);
        WriteInventoryProjects(fixture.Evidence.Root);
        var laneInventory = AcceptanceTestClassSourceScanner.Scan(fixture.Evidence.Root);
        Assert.Equal("Fixture.GoalRepositoriesCandidateIdentityTests", Assert.Single(laneInventory).FullName);
        Assert.NotEqual(GateObligationSelectorPreAdmissionClassifier.Kind.Resolved,
            Assert.Single(GateObligationSelectorPreAdmissionClassifier.Classify([selector], laneInventory)).Kind);

        var inventory = GateObligationSelectorPreAdmissionClassifier.ReadDeclaredInventory(fixture.Evidence.Root);
        Assert.Equal(new[] { "Fixture.AcceptanceCriteriaParserTests", "Fixture.CandidateIdentityTests",
            "Fixture.GoalRepositoriesCandidateIdentityTests" }, inventory.Select(item => item.FullName));
        Assert.Equal(GateObligationSelectorPreAdmissionClassifier.Kind.Resolved,
            Assert.Single(GateObligationSelectorPreAdmissionClassifier.Classify([selector], inventory)).Kind);
        var lines = new List<string>();
        var result = fixture.Apply(inventory, lines.Add);
        Assert.False(result.Hold);
        Assert.Empty(result.HoldLines);
        Assert.Empty(lines);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.DoesNotContain(fixture.Goal.Id.Value,
            ConductorBatchLoop.TrainIneligibleCriterionEvidenceGoalIds([fixture.Goal], new HashSet<string>()));
    }

    [Fact]
    public void Admission_MissingDeclaredProject_HoldsWithoutPartialInventoryRebind()
    {
        using var fixture = new Case("CandidateIdentityTests");
        WriteInventoryProjects(fixture.Evidence.Root);
        File.Delete(Path.Combine(fixture.Evidence.Root, "tests", "Core.Tests", "Core.Tests.csproj"));
        var result = GateObligationSelectorPreAdmissionClassifier.Apply(fixture.Kernel, fixture.Goal,
            fixture.Evidence.OrchestratorDirectory, Candidate,
            () => GateObligationSelectorPreAdmissionClassifier.ReadDeclaredInventory(fixture.Evidence.Root), _ => { });
        Assert.True(result.Hold);
        Assert.Contains("pre-admission classification unavailable", Assert.Single(result.HoldLines));
        Assert.Empty(fixture.Kernel.HumanInputRequests);
    }

    private static void WriteInventoryProjects(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), """
            {"engine":{"laneTestSourceRoots":["tests/Lane.Tests"]},"checks":[
              {"name":"lane","type":"dotnet-test","project":"tests/Lane.Tests/Lane.Tests.csproj"},
              {"name":"core","type":"dotnet-test","project":"tests/Core.Tests/Core.Tests.csproj"},
              {"name":"core duplicate","type":"dotnet-test","project":"tests/Core.Tests/Core.Tests.csproj"}
            ]}
            """);
        foreach (var project in new[] { "Lane.Tests", "Core.Tests" })
        {
            var directory = Path.Combine(root, "tests", project);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, project + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var classes = project == "Lane.Tests"
                ? new[] { "GoalRepositoriesCandidateIdentityTests" }
                : new[] { "CandidateIdentityTests", "AcceptanceCriteriaParserTests" };
            foreach (var name in classes)
                File.WriteAllText(Path.Combine(directory, name + ".cs"),
                    $"namespace Fixture; public class {name} {{ [Xunit.Fact] public void Runs() {{ }} }}");
        }
    }

    [Fact]
    public void ExistingHoldFormatter_PreservesSourceAndClosestIdentityText()
    {
        using var fixture = new Case("PlannedTests");
        var obligation = Assert.Single(fixture.Goal.GateTestObligations);
        Assert.Equal("gate test obligation obligati (brief v1) selector-not-found: PlannedTests; " +
            "source attempt; closest: ATests, BTests", LandingExecutor.FormatObligationHoldLine(
                obligation, "selector-not-found", ["PlannedTests"], "source attempt", ["ATests", "BTests"]));
        Assert.Equal("gate test obligation obligati (brief v1) pending: none; source none",
            LandingExecutor.FormatObligationHoldLine(obligation, "pending", [], "source none"));
    }

    internal static AcceptanceTestClassDescriptor[] Inventory(params string[] names) =>
        names.Select(name => new AcceptanceTestClassDescriptor(name, null)).ToArray();

    private static ConductorSpeculativeAcceptanceCandidate Ready(Goal goal)
    {
        return new(goal.Id, new GateReadyCandidateProjectionResult.Ready(new GateReadyCandidateProjection(
            goal.Id, GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, [$"src/{goal.Id.Value}.cs"], [$"resource:{goal.Id.Value}"],
            new(Candidate, Candidate, GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected))));
    }

    internal sealed class Case : IDisposable
    {
        internal ProspectiveEvidenceGateFixture Evidence { get; } = new();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal TaskSpec Developer => Assert.Single(Goal.Tasks);

        internal Case(params string[] selectors)
        {
            Evidence.Kernel.RecordGoalRefinement(Evidence.Goal.Id,
                new RefinedSpec(Evidence.Goal.Objective, ["Worker checks output"], VerificationClass.TestVerifiable, [], []));
            Kernel = GateTestObligationIntakeTests.Seed(Evidence.Kernel, Evidence.Goal, selectors);
            Goal = Kernel.GetGoal(Evidence.Goal.Id);
        }

        internal GateObligationSelectorPreAdmissionClassifier.Result Apply(
            IReadOnlyList<AcceptanceTestClassDescriptor>? inventory, Action<string>? emit = null,
            string candidateRevision = Candidate) =>
            GateObligationSelectorPreAdmissionClassifier.Apply(Kernel, Goal, Evidence.OrchestratorDirectory,
                candidateRevision, () => inventory, emit ?? (_ => { }));

        public void Dispose() => Evidence.Dispose();
    }
}
