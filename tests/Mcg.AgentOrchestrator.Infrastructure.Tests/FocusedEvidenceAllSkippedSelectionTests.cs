using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

// Parallel safe: each case owns a unique temporary TRX directory; no processes or shared state.
public sealed class FocusedEvidenceAllSkippedSelectionTests
{
    [Fact]
    public void MixedReceipt_AllSkippedSelectionIsCoveredWithoutAddingExecution()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("mixed.trx", ("AlphaTests.Runs", "Passed"),
            ("SkippedTests.First", "NotExecuted"), ("SkippedTests.Second", "NotExecuted"));

        var coverage = Inspect([path], "AlphaTests", "SkippedTests");
        var skipped = Assert.Single(coverage.AllSkippedSelections!);
        Assert.Equal("FullyQualifiedName~SkippedTests", skipped.Selection);
        Assert.Equal("all-skipped", Assert.Single(skipped.Reasons));
        Assert.Empty(coverage.UnreadableReceiptPaths);
        var trx = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence([path]);
        Assert.Equal(1, trx.ExecutedTestCount);
        Assert.Equal(2, trx.NotExecutedTestCount);
        Assert.Equal(new[] { "AlphaTests.Runs" },
            GoalAcceptanceVerifierTestTelemetry.ExtractTrxExecutedTestIdentities([path]).Identities);
        var decision = FocusedEvidenceAllSkippedSelectionDecision.Decide("mixed", true, trx.ExecutedTestCount, coverage);
        Assert.Null(decision.PolicyFailure);
        Assert.Null(decision.ApparatusDetail);
        Assert.Empty(decision.ZeroMatchSelections);
        Assert.True(GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
            new CommandResult(0, "completed"), trx, decision.PolicyFailure).Passed);

        var receipt = new AcceptanceCheckResult("mixed", true, 0, null,
            ExecutedTestCount: trx.ExecutedTestCount, AllSkippedSelections: coverage.AllSkippedSelections);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(receipt));
        Assert.Equal(1, json.RootElement.GetProperty("ExecutedTestCount").GetInt32());
        var reason = json.RootElement.GetProperty("AllSkippedSelections")[0];
        Assert.Equal("FullyQualifiedName~SkippedTests", reason.GetProperty("Selection").GetString());
        Assert.Equal("all-skipped", reason.GetProperty("Reasons")[0].GetString());
        Assert.Equal("; all-skipped: FullyQualifiedName~SkippedTests (all-skipped)",
            FocusedEvidenceAllSkippedSelection.FormatSummary([receipt]));
    }

    [Theory]
    [InlineData("notexecuted")]
    [InlineData(" notexecuted ")]
    public void Coverage_NormalizesNotExecutedOutcomes(string outcome)
    {
        using var fixture = new Fixture();
        var path = fixture.Write("normalized.trx", ("SkippedTests.First", outcome),
            ("SkippedTests.Second", outcome));

        var coverage = Inspect([path], "SkippedTests");

        var skipped = Assert.Single(coverage.AllSkippedSelections!);
        Assert.Equal("FullyQualifiedName~SkippedTests", skipped.Selection);
        Assert.Equal("all-skipped", Assert.Single(skipped.Reasons));
        Assert.Empty(coverage.UnreadableReceiptPaths);
    }

    [Fact]
    public void MissingSelection_RemainsZeroMatchBesideAnAllSkippedSelection()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("missing.trx", ("AlphaTests.Runs", "Passed"),
            ("SkippedTests.First", "NotExecuted"));

        var coverage = Inspect([path], "AlphaTests", "SkippedTests", "MisspeledTests");
        Assert.Equal("FullyQualifiedName~SkippedTests", Assert.Single(coverage.AllSkippedSelections!).Selection);
        var decision = FocusedEvidenceAllSkippedSelectionDecision.Decide("missing", true, 1, coverage);

        Assert.Equal("FullyQualifiedName~MisspeledTests", Assert.Single(decision.ZeroMatchSelections));
        Assert.Equal(AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, decision.PolicyFailure);
        Assert.Equal("'missing' had selection(s) matching 0 tests: FullyQualifiedName~MisspeledTests.", decision.ApparatusDetail);
        var trx = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence([path]);
        Assert.False(GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
            new CommandResult(0, "completed"), trx, decision.PolicyFailure).Passed);

        // The persisted detail already names the selection; the pre-Tester reason
        // must retain it even when its request/coverage context is missing.
        var check = new AcceptanceCheckResult("missing", false, 0, decision.ApparatusDetail,
            FailureClassification: decision.PolicyFailure, ExecutedTestCount: trx.ExecutedTestCount);
        var evidence = new FocusedEvidenceRunResult("request", true, false, "apparatus", [check]);
        var result = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence, true, "candidate", [], _ => false);
        Assert.Equal("evidence-invalid", result.Unusable!.Check);
        Assert.Equal("MisspeledTests", Assert.Single(result.Unusable.MissingClasses));
        Assert.Contains("unusable_classes=MisspeledTests",
            PreTesterUnusableReason.AppendTo(result.Unusable, "event"));
    }

    [Fact]
    public void OnlyNotExecutedResults_StillFailWithZeroExecutedTests()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("only-skipped.trx", ("SkippedTests.First", "NotExecuted"),
            ("SkippedTests.Second", "NotExecuted"));
        var coverage = Inspect([path], "SkippedTests");
        Assert.Single(coverage.AllSkippedSelections!);
        var trx = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence([path]);
        Assert.Equal(0, trx.ExecutedTestCount);
        Assert.Equal(2, trx.NotExecutedTestCount);
        Assert.Empty(GoalAcceptanceVerifierTestTelemetry.ExtractTrxExecutedTestIdentities([path]).Identities);

        var decision = FocusedEvidenceAllSkippedSelectionDecision.Decide("skipped", true, trx.ExecutedTestCount, coverage);

        Assert.Empty(decision.ZeroMatchSelections);
        Assert.Equal(AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, decision.PolicyFailure);
        Assert.Equal("'skipped' executed 0 tests.", decision.ApparatusDetail);
        Assert.False(GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
            new CommandResult(0, "completed"), trx, decision.PolicyFailure).Passed);
    }

    [Theory]
    [InlineData("Skipped", true)]
    [InlineData("Passed", false)]
    [InlineData("Failed", false)]
    public void MixedOutcomes_AcrossReceiptsCannotProveAllSkipped(string outcome, bool zeroMatch)
    {
        using var fixture = new Fixture();
        var first = fixture.Write("first.trx", ("AlphaTests.Runs", "Passed"),
            ("MixedTests.First", "NotExecuted"));
        var second = fixture.Write("second.trx", ("MixedTests.Second", outcome));

        var coverage = Inspect([first, second], "AlphaTests", "MixedTests");
        Assert.Empty(coverage.AllSkippedSelections!);
        var decision = FocusedEvidenceAllSkippedSelectionDecision.Decide("mixed", true, 1, coverage);
        if (zeroMatch)
        {
            Assert.Equal("FullyQualifiedName~MixedTests", Assert.Single(decision.ZeroMatchSelections));
            Assert.Equal(AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, decision.PolicyFailure);
        }
        else
        {
            Assert.Empty(decision.ZeroMatchSelections);
            Assert.Null(decision.PolicyFailure);
        }
    }

    [Fact]
    public void MethodSelection_UsesResolvedIdentityAndDeduplicatesTokens()
    {
        using var fixture = new Fixture();
        var first = fixture.Write("first.trx", ("Sample.SkippedTests.DoesNotRun", "NotExecuted"));
        var second = fixture.Write("second.trx", ("Sample.SkippedTests.DoesNotRun", "NotExecuted"));
        var token = new FocusedEvidenceFilterToken("method", "FullyQualifiedName~SkippedTests.DoesNotRun",
            FocusedEvidenceTokenKind.Method, "skippedtests.doesnotrun", "SkippedTests");

        var coverage = GoalAcceptanceVerifierTestTelemetry.InspectFocusedEvidenceSelectionCoverage(
            [[token], [token]], [first, second]);

        var skipped = Assert.Single(coverage.AllSkippedSelections!);
        Assert.Equal(token.CanonicalToken, skipped.Selection);
        Assert.Equal("all-skipped", Assert.Single(skipped.Reasons));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteReceipts_CannotSupplyAllSkippedProof(bool malformed)
    {
        using var fixture = new Fixture();
        var first = fixture.Write("first.trx", ("AlphaTests.Runs", "Passed"),
            ("SkippedTests.First", "NotExecuted"));
        var absent = Path.Combine(fixture.Root, "incomplete.trx");
        if (malformed) File.WriteAllText(absent, "<malformed");

        var coverage = Inspect([first, absent], "AlphaTests", "SkippedTests");

        Assert.Empty(coverage.AllSkippedSelections!);
        var decision = FocusedEvidenceAllSkippedSelectionDecision.Decide("incomplete", true, 1, coverage);
        Assert.Equal("FullyQualifiedName~SkippedTests", Assert.Single(decision.ZeroMatchSelections));
        Assert.Equal(malformed ? AcceptanceFailureClassifications.FocusedSelectionReceiptUnreadable :
            AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, decision.PolicyFailure);
        if (malformed)
        {
            Assert.Equal(absent, Assert.Single(coverage.UnreadableReceiptPaths));
            Assert.Equal($"'incomplete' could not read test receipt(s): {absent}.", decision.ApparatusDetail);
        }
    }

    [Fact]
    public void NoAllSkippedSelections_PreservesReceiptAndSummaryShape()
    {
        var receipt = new AcceptanceCheckResult("ordinary", true, 0, null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(receipt));
        Assert.False(json.RootElement.TryGetProperty("AllSkippedSelections", out _));
        Assert.Equal(string.Empty, FocusedEvidenceAllSkippedSelection.FormatSummary([receipt]));
    }

    private static FocusedEvidenceSelectionCoverage Inspect(string[] paths, params string[] classes) =>
        GoalAcceptanceVerifierTestTelemetry.InspectFocusedEvidenceSelectionCoverage(
            classes.Select(name => (IReadOnlyList<FocusedEvidenceFilterToken>)[new(
                name, "FullyQualifiedName~" + name, FocusedEvidenceTokenKind.Class, name, name)]).ToArray(), paths);

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("all-skipped-").FullName;

        internal string Write(string filename, params (string Identity, string Outcome)[] rows)
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var notExecuted = rows.Count(row => row.Outcome.Trim().Equals("NotExecuted", StringComparison.OrdinalIgnoreCase) ||
                row.Outcome.Equals("Skipped", StringComparison.OrdinalIgnoreCase));
            var path = Path.Combine(Root, filename);
            new XDocument(new XElement(ns + "TestRun",
                new XElement(ns + "TestDefinitions", rows.Select((row, index) =>
                    new XElement(ns + "UnitTest", new XAttribute("id", index),
                        new XElement(ns + "TestMethod",
                            new XAttribute("className", row.Identity[..row.Identity.LastIndexOf('.')]),
                            new XAttribute("name", row.Identity[(row.Identity.LastIndexOf('.') + 1)..]))))),
                new XElement(ns + "Results", rows.Select((row, index) =>
                    new XElement(ns + "UnitTestResult", new XAttribute("testId", index),
                        new XAttribute("testName", "friendly display " + index), new XAttribute("outcome", row.Outcome)))),
                new XElement(ns + "ResultSummary", new XAttribute("outcome", "Completed"),
                    new XElement(ns + "Counters", new XAttribute("total", rows.Length),
                        new XAttribute("executed", rows.Length - notExecuted),
                        new XAttribute("passed", rows.Count(row => row.Outcome == "Passed")),
                        new XAttribute("failed", rows.Count(row => row.Outcome == "Failed")),
                        new XAttribute("notExecuted", notExecuted)))))
                .Save(path);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
