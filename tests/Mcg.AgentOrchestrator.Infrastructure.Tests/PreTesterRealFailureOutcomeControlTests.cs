using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure classification over in-memory receipts.
public sealed class PreTesterRealFailureOutcomeControlTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Classify_RealFailure_PreservesRedAndNames(bool withCleanup, bool insideChanges)
    {
        var real = new AcceptanceCheckResult("focused", false, 1, "assertion failed",
            ExecutedTestCount: 1, FailingTestIdentities: ["Real.Tests.Fails"]);
        var cleanup = real with
        {
            FailureClassification = AcceptanceFailureClassifications.AssemblyCleanupFailure,
            FailingTestIdentities = ["Cleanup.Tests.Fails"]
        };
        var evidence = new FocusedEvidenceRunResult("request", true, false, "real failure", [],
            Arms: [new(FindingEvidenceArm.Candidate, "abc1234", FindingEvidenceArmDisposition.Red,
                true, false, "fixture", withCleanup ? [real, cleanup] : [real])]);

        var result = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence,
            true, "abc1234", [], _ => insideChanges);

        Assert.Equal(insideChanges ? "actionable-red" : "red", result.Outcome);
        Assert.Null(result.Unusable);
        Assert.Contains("Real.Tests.Fails", result.FailingTests);
        Assert.Equal(withCleanup ? 2 : 1, result.FailingTests.Count);
        if (withCleanup) Assert.Contains("Cleanup.Tests.Fails", result.FailingTests);
    }
}
