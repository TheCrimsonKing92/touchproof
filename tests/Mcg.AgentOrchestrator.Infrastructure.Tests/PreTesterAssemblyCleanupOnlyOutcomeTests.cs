using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure classification over in-memory receipts.
public sealed class PreTesterAssemblyCleanupOnlyOutcomeTests
{
    [Fact]
    public void Classify_CleanupOnlyWithManyNames_ReturnsUnusableWithoutNames()
    {
        var names = Enumerable.Range(0, 1036)
            .Select(index => $"Ns.C.M{index:D4} [Test Assembly Cleanup Failure (fixture)]").ToArray();
        var cleanup = new AcceptanceCheckResult("focused", false, 2, "cleanup failed",
            FailureClassification: AcceptanceFailureClassifications.AssemblyCleanupFailure,
            ExecutedTestCount: names.Length, FailingTestIdentities: names);
        var passed = new AcceptanceCheckResult("other selection", true, 0, "passed", ExecutedTestCount: 1);
        var evidence = new FocusedEvidenceRunResult("request", true, false, "cleanup-only", [],
            Arms: [new(FindingEvidenceArm.Candidate, "abc1234", FindingEvidenceArmDisposition.Red,
                true, false, "cleanup-only", [cleanup, cleanup with { Name = "second selection" }, passed])]);

        var result = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence,
            true, "abc1234", [], _ => throw new InvalidOperationException("Cleanup is not candidate attribution."));

        Assert.Equal("unusable", result.Outcome);
        Assert.Equal("assembly-cleanup-failure", Assert.IsType<PreTesterUnusableReason>(result.Unusable).Check);
        Assert.Empty(result.FailingTests);
        Assert.Equal(names.Length, Assert.IsType<FindingEvidenceArmReceipt>(result.Candidate).FailingTestIdentities.Count);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("passing")]
    [InlineData("different-sha")]
    [InlineData("baseline")]
    [InlineData("not-completed")]
    public void Classify_WithoutCompletedCandidateCleanup_DoesNotUseCleanupReason(string scenario)
    {
        var cleanup = new AcceptanceCheckResult("focused", false, 2, "cleanup failed",
            FailureClassification: AcceptanceFailureClassifications.AssemblyCleanupFailure,
            ExecutedTestCount: 1, FailingTestIdentities: ["Ns.C.M"]);
        AcceptanceCheckResult[] checks = scenario switch
        {
            "empty" => [],
            "passing" => [cleanup with { Passed = true, ExitCode = 0, FailingTestIdentities = [] }],
            _ => [cleanup]
        };
        var arm = new FocusedEvidenceArmRunResult(
            scenario == "baseline" ? FindingEvidenceArm.Baseline : FindingEvidenceArm.Candidate,
            scenario == "different-sha" ? "other-sha" : "abc1234",
            FindingEvidenceArmDisposition.Inconclusive, true, false, "fixture", checks);
        var evidence = new FocusedEvidenceRunResult("request", true, false, "fixture", [cleanup], Arms: [arm]);

        var result = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence,
            scenario != "not-completed", "abc1234", [], _ => false);

        Assert.Equal("unusable", result.Outcome);
        Assert.NotEqual("assembly-cleanup-failure", Assert.IsType<PreTesterUnusableReason>(result.Unusable).Check);
        if (scenario == "not-completed") Assert.Equal("not-completed", result.Unusable.Check);
    }
}
