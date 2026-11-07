using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class WorkerWriteIntegrityBlockedClassificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public void MediumIsEnvironmentalWithoutAutomaticRetry(bool? scopeComplete)
    {
        var verification = Verification(new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true))
            with { AssignedScopeComplete = scopeComplete };
        var outcome = DispatchFailureClassifier.Classify(Developer(), verification);

        Assert.Equal("worker-write-integrity-blocked", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Contains("src/Feature.cs", outcome.EvidenceSummary);
        Assert.Contains("Medium", outcome.EvidenceSummary);
    }

    [Theory]
    [InlineData(WorkerWriteIntegrityLabel.Low, true)]
    [InlineData(WorkerWriteIntegrityLabel.Medium, false)]
    [InlineData(WorkerWriteIntegrityLabel.Other, true)]
    [InlineData(WorkerWriteIntegrityLabel.Unlabeled, true)]
    public void InsufficientLabelEvidencePreservesIncompleteScope(WorkerWriteIntegrityLabel label, bool success)
    {
        AssertIncomplete(Verification(new("src/Feature.cs", label, success)));
    }

    [Fact]
    public void AbsentObservationPreservesIncompleteScope() => AssertIncomplete(Verification(null));

    [Fact]
    public void NonDeveloperIgnoresObservation()
    {
        var task = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var outcome = DispatchFailureClassifier.Classify(task,
            Verification(new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true)));
        Assert.NotEqual("worker-write-integrity-blocked", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Fact]
    public void EarlierFailingTestsKeepPrecedence()
    {
        var verification = Verification(new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true)) with
        {
            StandardOutput = Verification(null).StandardOutput.Replace(
                "tests: deferred - conductor", "tests: fail - focused regression failed", StringComparison.Ordinal)
        };
        var outcome = DispatchFailureClassifier.Classify(Developer(), verification);
        Assert.Equal("succeeded-worker-result-failing-tests", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    [Fact]
    public void SameRoundEnrichmentRetainsTransientObservation()
    {
        var plain = Verification(null);
        var observed = plain with { WriteIntegrityObservation = new("src/Feature.cs", WorkerWriteIntegrityLabel.Medium, true) };
        Assert.Equal(observed.WriteIntegrityObservation, plain.MergeSameRoundEnrichment(observed).WriteIntegrityObservation);
        Assert.Equal(observed.WriteIntegrityObservation, observed.MergeSameRoundEnrichment(plain).WriteIntegrityObservation);
    }

    private static TaskSpec Developer() => new(TaskId.New(), "Implement", AgentRole.Developer);

    private static void AssertIncomplete(TaskVerificationRecord verification)
    {
        var outcome = DispatchFailureClassifier.Classify(Developer(), verification);
        Assert.Equal("incomplete-scope-declaration", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
    }

    private static TaskVerificationRecord Verification(WorkerWriteIntegrityObservation? observation) => new(
        "worker", "C:\\repo", 0, """
        WORKER_RESULT:
        files: none
        commands: none
        tests: deferred - conductor
        commit: none
        blockers: exact-blocker - Cannot write src/Feature.cs; integrity mismatch claims Medium
        assigned_scope_complete: false
        model_fit: fixture/model - adequate - classification
        skills: none
        confidence: high
        END_WORKER_RESULT
        """, "", DateTimeOffset.Parse("2026-11-07T12:00:00Z"), WorkerResultPresent: true, AssignedScopeComplete: false)
    { WriteIntegrityObservation = observation };
}
