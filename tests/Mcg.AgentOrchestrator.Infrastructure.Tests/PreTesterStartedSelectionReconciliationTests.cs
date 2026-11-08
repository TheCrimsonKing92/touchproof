using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure selection reconciliation with a private progress sink.
public sealed class PreTesterStartedSelectionReconciliationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EqualSetsPreserveExactRecordingAndRequestIdentity(bool duplicateRecorded, bool duplicateRecomputed)
    {
        var recorded = new[] { "Infrastructure.Tests:GuardTests", "Core.Tests:DeclaredTests" }.ToList();
        if (duplicateRecorded) recorded.Add(recorded[0]);
        var prior = Started(recorded, ["SecondMissingTests", "FirstMissingTests", "SecondMissingTests"]);
        var recomputed = new List<FindingEvidenceSelection>
        {
            new("core.tests", "declaredtests"), new("infrastructure.tests", "guardtests")
        };
        if (duplicateRecomputed) recomputed.Add(recomputed[0]);
        var lines = new List<string>();

        var result = PreTesterGuardSelections.ReconcileStarted(prior, recomputed,
            ["FirstMissingTests", "SecondMissingTests"], Format, lines.Add);

        Assert.Equal(recorded, result.Selections.Select(Format));
        Assert.Equal(string.Join("|", recorded), FindingEvidenceExecutionClassifier.BuildRequestIdentity(new(result.Selections)));
        Assert.Same(prior.NotRun, result.NotRun);
        Assert.Empty(lines);
        Assert.Equal(recorded, prior.Selections);
    }

    [Fact]
    public void SelectionDriftNamesMissingAndAddedInSourceOrderInBothDiagnostics()
    {
        var prior = Started(["Core.Tests:SecondMissingTests", "Core.Tests:FirstMissingTests", "Core.Tests:SecondMissingTests"]);
        var lines = new List<string>();

        var exception = Assert.Throws<InvalidDataException>(() => PreTesterGuardSelections.ReconcileStarted(prior,
            [new("Core.Tests", "SecondAddedTests"), new("Core.Tests", "FirstAddedTests"), new("core.tests", "secondaddedtests")],
            [], Format, lines.Add));

        const string detail = "missing=Core.Tests:SecondMissingTests,Core.Tests:FirstMissingTests " +
            "added=Core.Tests:SecondAddedTests,Core.Tests:FirstAddedTests not_run_missing=<none> not_run_added=<none>";
        Assert.Equal("Started pre-Tester evidence selection changed before reconciliation. " + detail, exception.Message);
        Assert.Equal("PRE_TESTER_STARTED_SELECTION_DRIFT receipt=recorded-receipt " + detail, Assert.Single(lines));
    }

    [Theory]
    [InlineData("MissingTests", "OtherTests")]
    [InlineData("MissingTests", "missingtests")]
    public void NotRunOnlyDriftUsesOrdinalEqualityAndExplainsBothDiagnostics(string recorded, string recomputed)
    {
        var prior = Started(["Core.Tests:DeclaredTests"], [recorded]);
        var lines = new List<string>();

        var exception = Assert.Throws<InvalidDataException>(() => PreTesterGuardSelections.ReconcileStarted(prior,
            [new("Core.Tests", "DeclaredTests")], [recomputed], Format, lines.Add));

        var detail = $"missing=<none> added=<none> not_run_missing={recorded} not_run_added={recomputed}";
        Assert.Equal("Started pre-Tester evidence selection changed before reconciliation. " + detail, exception.Message);
        Assert.Equal("PRE_TESTER_STARTED_SELECTION_DRIFT receipt=recorded-receipt " + detail, Assert.Single(lines));
        Assert.Equal([recorded], prior.NotRun);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneSidedSelectionDriftStillThrows(bool missing)
    {
        var prior = Started(missing ? ["Core.Tests:DeclaredTests", "Core.Tests:RemovedTests"] : ["Core.Tests:DeclaredTests"]);
        FindingEvidenceSelection[] recomputed = missing ? [new("Core.Tests", "DeclaredTests")] :
            [new("Core.Tests", "DeclaredTests"), new("Core.Tests", "AddedTests")];
        var lines = new List<string>();

        var exception = Assert.Throws<InvalidDataException>(() =>
            PreTesterGuardSelections.ReconcileStarted(prior, recomputed, [], Format, lines.Add));

        var detail = missing ? "missing=Core.Tests:RemovedTests added=<none>" : "missing=<none> added=Core.Tests:AddedTests";
        Assert.Contains(detail, exception.Message, StringComparison.Ordinal);
        Assert.Contains(detail, Assert.Single(lines), StringComparison.Ordinal);
    }

    private static PreTesterEvidenceEntry Started(IReadOnlyList<string> selections, IReadOnlyList<string>? notRun = null) =>
        new("started", "bbb2222", "recorded-receipt", selections, notRun ?? [], null, []);

    private static string Format(FindingEvidenceSelection selection) => ConductorDriver.FormatFindingEvidenceSelection(selection);
}
