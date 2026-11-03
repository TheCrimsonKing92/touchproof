using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all artifacts and kernel state belong to the fixture.
public sealed class GateTestSelectorDiagnosticTests
{
    [Fact]
    public void DeclarationRecognizesClassWithoutClaimingExecution()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var goal = Seed(f, "ExactTests");
        var entry = Assert.Single(Project(goal, f.CandidateSha,
            [new("tests/project.csproj", "declared classes", f.CandidateSha, goal.AuthoritativeBrief.Version,
                null, [new("ExactTests", "ExactTests", "ExactTests", null, "tests/ExactTests.cs")], [], Complete: false)]));
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Resolved, entry.Status);
        Assert.Contains(entry.Evidence, text => text.Contains("declared classes") && text.Contains("available/current"));
        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending,
            Assert.Single(GateTestObligationEvaluator.Evaluate(goal, f.OrchestratorDirectory, f.CandidateSha)).Outcome);
        Assert.Equal(new[] { "ExactTests" }, Assert.Single(goal.GateTestObligations).Selectors);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("stale candidate")]
    [InlineData("stale brief")]
    public void UnavailableEvidenceRemainsUnknown(string mode)
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var goal = Seed(f, "PredictedTests");
        var source = Evidence(goal, f.CandidateSha, "PredictedTests.Check") with
        {
            Candidate = mode == "stale candidate" ? new string('b', 40) : f.CandidateSha,
            BriefVersion = mode == "stale brief" ? goal.AuthoritativeBrief.Version + 1 : goal.AuthoritativeBrief.Version,
            UnavailableReason = mode is "missing" or "unreadable" ? mode : null
        };
        var entry = Assert.Single(Project(goal, f.CandidateSha, [source]));
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Unknown, entry.Status);
        Assert.Empty(entry.Suggestions);
        Assert.Equal("no reliable suggestions available", entry.SuggestionStatus);
        Assert.Contains(entry.Evidence, text => text.Contains(mode == "stale brief" ? "brief binding" : mode));
    }

    [Fact]
    public void MissingAndMalformedFilesDoNotBecomeEmptyIdentityProof()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var path = Path.Combine(f.Root, "broken.trx");
        var source = new GateTestObligationEvaluator.Source("receipt", DateTimeOffset.UtcNow, [path], 1);
        var missing = GateTestSelectorDiagnosticPreparation.ReadReceipt(source, f.Root, f.CandidateSha);
        Assert.Contains("missing", missing.UnavailableReason!);
        File.WriteAllText(path, "<broken");
        var unreadable = GateTestSelectorDiagnosticPreparation.ReadReceipt(source, f.Root, f.CandidateSha);
        Assert.Contains("unreadable", unreadable.UnavailableReason!);
        using var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Contains("unreadable", GateTestSelectorDiagnosticPreparation.ReadReceipt(source, f.Root, f.CandidateSha).UnavailableReason!);
    }

    [Fact]
    public void MissingDeclaredReportCannotBorrowUnrelatedExecutionIdentities()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var path = Path.Combine(f.Root, "unrelated.ctrf.json");
        File.WriteAllText(path, """
            {"results":{"tests":[{"testId":"OtherTests.Check","name":"Check","status":"passed"}]}}
            """);
        var checks = new ProspectiveEvidenceGateResolver.GateCheckOutcomes(
            [new() { Name = DeclaredProjectDiscoveryAdapter.TestCheckName, Type = "junit-command" }],
            new Dictionary<string, bool>(), false);
        var source = new GateTestObligationEvaluator.Source("declared", DateTimeOffset.UtcNow, [path], 1, Checks: checks);
        var evidence = GateTestSelectorDiagnosticPreparation.ReadReceipt(source, f.Root, f.CandidateSha);
        Assert.NotNull(evidence.UnavailableReason);
        Assert.Empty(evidence.Rows);
        Assert.Empty(evidence.DeclaredRows);
    }

    [Fact]
    public void SuggestionsAreBoundedDeterministicAndCannotSatisfyOrRewrite()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var goal = Seed(f, "PredictedTests");
        var before = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, f.OrchestratorDirectory, f.CandidateSha));
        var rows = Enumerable.Range(0, 8).Select(i => $"ActualTests.Case{i}").Reverse().ToArray();
        var evidence = Evidence(goal, f.CandidateSha, rows);
        var report = new GateTestSelectorDiagnostic.Report("accepted Developer", f.CandidateSha, true, ["PredictedTests", rows[0]]);
        var entry = Assert.Single(Project(goal, f.CandidateSha, [evidence], report));
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Unresolved, entry.Status);
        Assert.Equal(5, entry.Suggestions.Count);
        Assert.True(entry.Ambiguous);
        Assert.True(entry.Truncated);
        Assert.Contains("PredictedTests", entry.Suggestions);
        var reversed = evidence with { Rows = evidence.Rows.Reverse().ToArray() };
        Assert.Equal(entry.Suggestions, Assert.Single(Project(goal, f.CandidateSha, [reversed], report)).Suggestions);
        var after = Assert.Single(GateTestObligationEvaluator.Evaluate(goal, f.OrchestratorDirectory, f.CandidateSha));
        Assert.Equal(before, after);
        Assert.Equal(new[] { "PredictedTests" }, Assert.Single(goal.GateTestObligations).Selectors);
        Assert.Empty(goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived));
    }

    [Theory]
    [InlineData("src/game/engine.test.ts::battle engine > applies damage")]
    [InlineData("battle engine > applies damage")]
    [InlineData("npx eslint .")]
    public void PreservesDeclaredNonCSharpAndCommandSelectors(string selector)
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var goal = Seed(f, selector);
        var row = new DeclaredTestReportRows.Row("src/game/engine.test.ts::battle engine > applies damage",
            "battle engine > applies damage", [], "NotExecuted", "declarations.json");
        var checks = new ProspectiveEvidenceGateResolver.GateCheckOutcomes(
            [new() { Name = "lint", Type = "command", Command = "npx", Arguments = ["eslint", "."] }],
            new Dictionary<string, bool>(), false);
        var entry = Assert.Single(Project(goal, f.CandidateSha,
            [new("declarations", "declared identities/commands", f.CandidateSha, goal.AuthoritativeBrief.Version,
                null, [], [row], checks)]));
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Resolved, entry.Status);
        Assert.Equal(selector, entry.EffectiveSelector);
    }

    [Fact]
    public void TestProducingCommandNeedsIdentityEvidence()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var goal = Seed(f, "dotnet test");
        var checks = new ProspectiveEvidenceGateResolver.GateCheckOutcomes(
            [new() { Name = "dotnet test", Type = "dotnet-test" }], new Dictionary<string, bool>(), false);
        var entry = Assert.Single(Project(goal, f.CandidateSha,
            [new("manifest", "declared checks", f.CandidateSha, goal.AuthoritativeBrief.Version,
                null, [], [], checks, Complete: false)]));
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Unknown, entry.Status);
        Assert.Equal(GateCheckSelectorMatcher.Outcome.NotApplicable, GateCheckSelectorMatcher.Grade("dotnet test", checks));
    }

    [Fact]
    public void AuthorizedExactRebindRetainsOriginalAndPlannerProvenance()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var kernel = GateTestObligationIntakeTests.Seed(f.Kernel, f.Goal, ["OldTests"]);
        var goal = kernel.GetGoal(f.Goal.Id);
        var obligation = Assert.Single(goal.GateTestObligations);
        var request = kernel.RecordAnsweredOperatorHumanInput(goal.Id, "Bind the selector",
            AnsweredSelectorRebinding.FormatSelectorEdit("OldTests", "NewTests", [obligation.Id]));
        var entry = Assert.Single(GateTestSelectorDiagnostic.Project(goal, f.CandidateSha,
            [Evidence(goal, f.CandidateSha, "NewTests.Check")], null, kernel.HumanInputRequests));
        Assert.Equal(GateTestSelectorDiagnostic.Resolution.Resolved, entry.Status);
        Assert.Equal(new[] { "OldTests" }, entry.OriginalSelectors);
        Assert.Equal("NewTests", entry.EffectiveSelector);
        Assert.Equal(request.Id.Value, Assert.Single(entry.AuthorizedEdits).RequestId);
        Assert.Equal(new[] { "OldTests" }, obligation.Selectors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleReportCannotSupplySuggestions(bool staleCandidate)
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var goal = Seed(f, "PredictedTests");
        var stale = new GateTestSelectorDiagnostic.Report("old Developer",
            staleCandidate ? new string('b', 40) : f.CandidateSha, staleCandidate, ["ReportOnlyTests"]);
        var entry = Assert.Single(Project(goal, f.CandidateSha, [], stale));
        Assert.Empty(entry.DeveloperAssertions);
        Assert.Empty(entry.Suggestions);
        Assert.Null(entry.CriterionIndex); // Never invent a criterion from list position.
        Assert.Equal(goal.AuthoritativeBrief.Version, entry.BriefVersion);
        Assert.Equal(f.CandidateSha, entry.Candidate);
    }

    [Fact]
    public void CurrentBriefProjectionPreservesCriterionAndPlannerIdentity()
    {
        using var f = new ProspectiveEvidenceGateFixture();
        var version = f.Goal.AuthoritativeBrief.Version;
        var identity = new PlannerEvidenceIdentity(f.Goal.Id.Value, "planner-task", version, 2, "coverage");
        var payload = new PlannerEvidencePayload(2, "coverage", "post-implementation", "acceptance-gate",
            "Prospective coverage", "Awaiting candidate evidence", null, null, ["PredictedTests"]);
        var obligation = new GateTestObligation(identity.StableId, version, ["PredictedTests"], "planner-task",
            "Prospective coverage", DateTimeOffset.UtcNow, identity, payload);
        var oldIdentity = identity with { BriefVersion = version - 1 };
        var oldObligation = obligation with
        {
            Id = oldIdentity.StableId, BriefVersion = oldIdentity.BriefVersion, PlannerIdentity = oldIdentity
        };
        var snapshot = f.Kernel.ExportSnapshot();
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                GateTestObligations = [obligation, oldObligation]
            }).ToArray()
        });
        var entry = Assert.Single(Project(kernel.GetGoal(f.Goal.Id), f.CandidateSha, []));
        Assert.Equal(2, entry.CriterionIndex);
        Assert.Equal(identity.StableId, entry.PlannerIdentity);
        Assert.Equal("planner-task", entry.RequestingTaskId);
        Assert.Equal(obligation.Selectors, entry.OriginalSelectors);
        Assert.Equal(obligation.Id, entry.ObligationId);
        Assert.Equal(version, entry.BriefVersion);
    }

    private static Goal Seed(ProspectiveEvidenceGateFixture f, string selector) =>
        GateTestObligationIntakeTests.Seed(f.Kernel, f.Goal, [selector]).GetGoal(f.Goal.Id);

    private static GateTestSelectorDiagnostic.Evidence Evidence(Goal goal, string candidate, params string[] names) =>
        new("receipt.trx", "executed receipt", candidate, goal.AuthoritativeBrief.Version, null,
            names.Select(name => new ProspectiveEvidenceTrxRows.Row(name, name, null, "Passed", "receipt.trx")).ToArray(), []);

    private static IReadOnlyList<GateTestSelectorDiagnostic.Entry> Project(Goal goal, string candidate,
        IReadOnlyList<GateTestSelectorDiagnostic.Evidence> evidence, GateTestSelectorDiagnostic.Report? report = null) =>
        GateTestSelectorDiagnostic.Project(goal, candidate, evidence, report, []);
}
