using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// No external artifacts or shared state; parallel-safe.
public sealed class PreTesterApparatusReceiptClassifierTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InvalidEvidenceRetainsZeroMatchNamesWithoutMatchingRequestOrCoverage(bool inArm, bool unmatched)
    {
        var check = new AcceptanceCheckResult("selection", false, 0,
            "Focused evidence selection apparatus failure: 'selection' had selection(s) matching 0 tests: " +
            "FullyQualifiedName~OwnerConsoleHarness, FullyQualifiedName~CliAttentionShowDecisionsFixture.",
            FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
            ExecutedTestCount: 243);
        var evidence = new FocusedEvidenceRunResult("request", true, false, "apparatus", inArm ? [] : [check],
            Arms: inArm ? [new(FindingEvidenceArm.Candidate, "candidate", FindingEvidenceArmDisposition.Inconclusive,
                true, false, "apparatus", [check])] : null);
        FindingEvidenceSelection[] selections = unmatched ? [new("Other.Tests", "UnrelatedTests")] : [];

        var result = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence, true, "candidate", selections, _ => false);

        Assert.Equal("unusable", result.Outcome);
        Assert.Equal("evidence-invalid", result.Unusable!.Check);
        Assert.Equal(["CliAttentionShowDecisionsFixture", "OwnerConsoleHarness"], result.Unusable.MissingClasses);
        Assert.Contains("unusable_classes=CliAttentionShowDecisionsFixture,OwnerConsoleHarness",
            PreTesterUnusableReason.AppendTo(result.Unusable, "event"));
    }

    [Theory]
    [InlineData("focused-selection-apparatus-failure", false, "'selection' executed 0 tests.")]
    [InlineData("focused-selection-apparatus-failure", false, "'selection' had selection(s) matching 0 tests: .")]
    [InlineData("build-phase-timeout", false, "'selection' had selection(s) matching 0 tests: FullyQualifiedName~Helper.")]
    [InlineData("focused-selection-apparatus-failure", true, "'selection' had selection(s) matching 0 tests: FullyQualifiedName~Helper.")]
    public void MissingNamesAndUnrelatedFailuresDoNotInventAffectedClasses(string classification, bool passed, string detail)
    {
        var check = new AcceptanceCheckResult("selection", passed, 0, detail,
            FailureClassification: classification, ExecutedTestCount: 0);
        var evidence = new FocusedEvidenceRunResult("request", true, false, "invalid", [check]);
        var result = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence, true, "candidate",
            [new("Example.Tests", "UnprovenTests")], _ => false);
        Assert.Equal("evidence-invalid", result.Unusable!.Check);
        Assert.Empty(result.Unusable.MissingClasses);
    }

    [Theory]
    [InlineData("build-only", true)]
    [InlineData("mixed-failures", false)]
    [InlineData("non-build", false)]
    [InlineData("executed", false)]
    [InlineData("all-passed", false)]
    [InlineData(null, false)]
    public void BuildPhaseOnlyNeedsEveryFailedCheckToBeABuildTimeoutWithZeroExecuted(string? scenario, bool expected)
    {
        var build = new AcceptanceCheckResult("build", false, -1, "no tests",
            FailureClassification: AcceptanceFailureClassifications.BuildPhaseTimeout, ExecutedTestCount: 0);
        var apparatus = build with { FailureClassification = AcceptanceFailureClassifications.FocusedSelectionApparatusFailure };
        var passed = build with { Passed = true, ExitCode = 0, FailureClassification = null };
        FocusedEvidenceRunResult Evidence(params AcceptanceCheckResult[] checks) =>
            new("request", true, false, "fixture", checks);
        var evidence = scenario switch
        {
            "build-only" => Evidence(build),
            "mixed-failures" => Evidence(build, apparatus),
            "non-build" => Evidence(apparatus),
            "executed" => Evidence(build, passed with { ExecutedTestCount = 3 }),
            "all-passed" => Evidence(passed),
            null => null,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        Assert.Equal(expected, PreTesterApparatusReceiptClassifier.IsBuildPhaseOnly(evidence));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public void BuildPhaseOnlyReadsBothCheckLocations(bool inArm, int? executed)
    {
        var check = new AcceptanceCheckResult("build", false, -1, "no tests",
            FailureClassification: AcceptanceFailureClassifications.BuildPhaseTimeout, ExecutedTestCount: executed);
        var evidence = new FocusedEvidenceRunResult("request", true, false, "fixture", inArm ? [] : [check],
            Arms: inArm ? [new(FindingEvidenceArm.Candidate, "abc1234", FindingEvidenceArmDisposition.Inconclusive,
                true, false, "fixture", [check])] : null);
        Assert.True(PreTesterApparatusReceiptClassifier.IsBuildPhaseOnly(evidence));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void BuildPhaseOnlyRejectsIdentitiesOrExecutionInEveryCheck(bool inArm, bool passed, bool identity)
    {
        var build = new AcceptanceCheckResult("build", false, -1, "no tests",
            FailureClassification: AcceptanceFailureClassifications.BuildPhaseTimeout, ExecutedTestCount: 0);
        var disqualifier = build with { Passed = passed, ExecutedTestCount = identity ? 0 : 3,
            FailingTestIdentities = identity ? ["Tests.Fails"] : [] };
        var evidence = new FocusedEvidenceRunResult("request", true, false, "fixture", inArm ? [build] : [build, disqualifier],
            Arms: inArm ? [new(FindingEvidenceArm.Baseline, "aaa1111", FindingEvidenceArmDisposition.Inconclusive,
                true, false, "fixture", [disqualifier])] : null);
        Assert.False(PreTesterApparatusReceiptClassifier.IsBuildPhaseOnly(evidence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildPhaseOnlyRejectsNonBuildFailuresAcrossCheckLocations(bool buildInArm)
    {
        var build = new AcceptanceCheckResult("build", false, -1, "no tests",
            FailureClassification: AcceptanceFailureClassifications.BuildPhaseTimeout, ExecutedTestCount: 0);
        var other = build with { FailureClassification = AcceptanceFailureClassifications.FocusedSelectionApparatusFailure };
        var evidence = new FocusedEvidenceRunResult("request", true, false, "fixture", [buildInArm ? other : build],
            Arms: [new(FindingEvidenceArm.Candidate, "abc1234", FindingEvidenceArmDisposition.Inconclusive,
                true, false, "fixture", [buildInArm ? build : other])]);
        Assert.False(PreTesterApparatusReceiptClassifier.IsBuildPhaseOnly(evidence));
    }

    [Fact]
    public void EmptyChecksAreNotBuildPhaseOnly() =>
        Assert.False(PreTesterApparatusReceiptClassifier.IsBuildPhaseOnly(
            new FocusedEvidenceRunResult("request", true, false, "empty", [])));

    [Fact]
    public void MissingEvidenceDoesNotAuthorizeRefresh() =>
        Assert.False(PreTesterApparatusReceiptClassifier.IsApparatusUnusable(null));

    [Theory]
    [InlineData(FindingEvidenceArmDisposition.Inconclusive, 0, false, true)]
    [InlineData(FindingEvidenceArmDisposition.Inconclusive, null, false, true)]
    [InlineData(FindingEvidenceArmDisposition.Green, 0, false, false)]
    [InlineData(FindingEvidenceArmDisposition.Red, 1, true, false)]
    [InlineData(FindingEvidenceArmDisposition.Inconclusive, 1, false, false)]
    public void EmptyNonGreenCandidateIsUnusable(FindingEvidenceArmDisposition disposition,
        int? executed, bool failing, bool expected)
    {
        var check = new AcceptanceCheckResult("selection", disposition == FindingEvidenceArmDisposition.Green,
            0, "fixture", ExecutedTestCount: executed, FailingTestIdentities: failing ? ["Tests.Fails"] : []);
        var evidence = new FocusedEvidenceRunResult("request", true, check.Passed, "fixture", [],
            Arms: [new(FindingEvidenceArm.Candidate, "abc1234", disposition, true, check.Passed, "fixture", [check])]);
        Assert.Equal(expected, PreTesterApparatusReceiptClassifier.IsApparatusUnusable(evidence));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void ApparatusCheckIsPositiveEvidence(bool inArm, bool passed, bool expected)
    {
        var check = new AcceptanceCheckResult("build", passed, -1, "no tests",
            FailureClassification: AcceptanceFailureClassifications.BuildPhaseTimeout);
        var evidence = new FocusedEvidenceRunResult("request", true, false, "fixture", inArm ? [] : [check],
            Arms: inArm ? [new(FindingEvidenceArm.Baseline, "aaa1111", FindingEvidenceArmDisposition.Inconclusive,
                true, false, "fixture", [check])] : null);
        Assert.Equal(expected, PreTesterApparatusReceiptClassifier.IsApparatusUnusable(evidence));
    }

    [Fact]
    public void MissingCandidateWithoutApparatusCheckDoesNotAuthorizeRefresh() =>
        Assert.False(PreTesterApparatusReceiptClassifier.IsApparatusUnusable(
            new FocusedEvidenceRunResult("request", true, false, "incomplete", [])));
}
