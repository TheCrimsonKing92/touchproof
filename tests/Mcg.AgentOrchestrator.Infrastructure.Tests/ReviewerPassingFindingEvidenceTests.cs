using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: all I/O and state are scoped by the fixture; no child process is launched.
public sealed class ReviewerPassingFindingEvidenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DeliveredGreen_RepeatedIncidentFinding_RoutesOneSourceCorrection(bool capture, bool legacyContext)
    {
        using var fixture = new ReviewerPassingFindingEvidenceFixture(capture
            ? "7b50a02394baa03c45d5f557e6a3f8df21f0e530" : "df1cbfc3a1535e3a062ab7fdda0b114c7653c716");
        var finding = capture ? ReviewerPassingFindingEvidenceFixture.CaptureFinding() : ReviewerPassingFindingEvidenceFixture.CliFinding();
        var original = fixture.SeedPreTesterCoverage(finding.EvidenceRequest!);
        Assert.True(original.Request.Selections.Count > finding.EvidenceRequest!.Selections.Count);
        Assert.Empty(fixture.Receipts);
        fixture.Report([finding]);
        fixture.AdvanceToDecision();
        Assert.Equal(fixture.Reviewer.Id, Assert.Single(fixture.Retries).Task);
        Assert.Empty(fixture.Executions);
        var receipt = Assert.Single(fixture.Receipts);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(receipt));
        Assert.Equal("pre-tester-deferred", receipt.FindingRoundFingerprint);
        Assert.True(receipt.Passed);
        Assert.Equal(fixture.Candidate, receipt.CandidateSha);
        Assert.Equal(1, fixture.ReviewDispatches);
        Assert.Contains("receipt_ids=" + receipt.ReceiptId, fixture.Retries[0].Feedback);
        var delivered = fixture.Reviewer.VerificationHistory.Last();
        var disposition = Assert.Single(delivered.FindingEvidenceRequestDispositions!,
            item => item.FindingStableId == finding.StableId);
        Assert.Equal(FindingEvidenceRequestDecision.CoveredByReceipt, disposition.Decision);
        Assert.Equal(receipt.ReceiptId, Assert.Single(disposition.SourceReceiptIds!));
        Assert.Contains(fixture.Goal.Timeline, item => item.TaskId == fixture.Reviewer.Id &&
            item.Message.StartsWith("finding-evidence disposition=reused-covered-green;", StringComparison.Ordinal));

        fixture.Report([finding], legacyContext: legacyContext);
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(fixture.Reviewer.LastVerification, out var round, out _));
        var retained = Assert.Single(round.Findings);
        // Pin the incident's loop branch: an executed Reviewer-owned request would already
        // take the ordinary Developer retry route without the passing-evidence router.
        Assert.Equal(FindingEvidenceExecutionState.PendingExecution,
            FindingEvidenceExecutionClassifier.Classify(fixture.Reviewer, retained, fixture.Candidate));
        fixture.AdvanceToDecision();

        Assert.Equal(2, fixture.ReviewDispatches);
        Assert.Empty(fixture.Executions);
        Assert.Equal(2, fixture.Retries.Count);
        Assert.Equal(1, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
            fixture.Goal.Timeline, fixture.Reviewer.Id, fixture.Candidate, finding.StableId));
        var correction = fixture.Retries[1];
        Assert.Equal(fixture.Developer.Id, correction.Task);
        Assert.Equal(RetryCause.NewSourceFinding, correction.Cause);
        Assert.Contains("REVIEWER_PASSING_EVIDENCE_CORRECTION", correction.Feedback);
        Assert.Contains(finding.StableId, correction.Feedback);
        Assert.Contains(finding.Description, correction.Feedback);
        Assert.Contains(finding.Location.ToString(), correction.Feedback);
        Assert.Contains(receipt.ReceiptId, correction.Feedback);
        Assert.Contains(fixture.Candidate, correction.Feedback);
        Assert.Contains("review_dispatch=", correction.Feedback);
        Assert.Contains("review_completed=", correction.Feedback);
        var remaining = Assert.Single(fixture.Reviewer.VerificationHistory.Last().MergedReviewFindings!);
        Assert.Equal(finding.StableId, remaining.StableId);
        Assert.Equal(ReviewFindingState.Open, remaining.State);
    }

    [Theory]
    [InlineData(FindingCategory.Correctness)]
    [InlineData(FindingCategory.TestEvidence)]
    public void ConsumedEvidence_NewSourceBlocker_PreservesNormalReviewRoute(FindingCategory category)
    {
        using var fixture = new ReviewerPassingFindingEvidenceFixture();
        var evidenceFinding = ReviewerPassingFindingEvidenceFixture.CliFinding() with { Category = category };
        fixture.Report([evidenceFinding]);
        fixture.AdvanceToDecision();
        Assert.Equal(fixture.Reviewer.Id, Assert.Single(fixture.Retries).Task);
        var receipt = Assert.Single(fixture.Receipts);
        Assert.True(receipt.Passed);
        var sourceFinding = ReviewerPassingFindingEvidenceFixture.CaptureFinding() with { EvidenceRequest = null };

        fixture.Report([evidenceFinding, sourceFinding]);
        Assert.False(fixture.Decide(out _));
        fixture.AdvanceToDecision();

        Assert.Equal(2, fixture.ReviewDispatches);
        Assert.Single(fixture.Executions);
        Assert.Equal(2, fixture.Retries.Count);
        var retry = fixture.Retries[1];
        Assert.Equal(fixture.Developer.Id, retry.Task);
        Assert.StartsWith("auto-review-retry round ", retry.Feedback);
        Assert.Contains(evidenceFinding.StableId, retry.Feedback);
        Assert.Contains(evidenceFinding.Location.ToString(), retry.Feedback);
        Assert.Contains(sourceFinding.StableId, retry.Feedback);
        Assert.Contains(sourceFinding.Description, retry.Feedback);
        Assert.DoesNotContain("REVIEWER_PASSING_EVIDENCE_CORRECTION", retry.Feedback);
        var findings = fixture.Reviewer.VerificationHistory.Last(record => record.MergedReviewFindings is { Count: > 0 })
            .MergedReviewFindings!;
        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(ReviewFindingState.Open, finding.State));
    }

    [Fact]
    public void ConsumedEvidence_LegacyOwnership_PreservesTesterReviewRoute()
    {
        using var fixture = new ReviewerPassingFindingEvidenceFixture();
        var finding = ReviewerPassingFindingEvidenceFixture.CliFinding() with { Category = FindingCategory.TestEvidence };
        fixture.Report([finding]);
        fixture.AdvanceToDecision();
        Assert.Equal(fixture.Reviewer.Id, Assert.Single(fixture.Retries).Task);
        fixture.Report([finding with
        {
            Category = FindingCategory.Unspecified,
            Description = "Tester must inspect the delivered test receipt; the remaining scenario is missing."
        }]);
        Assert.True(Mcg.AgentOrchestrator.App.Orchestration.ReviewerFindingEvidenceSuppressionRouting.Resolve(
            fixture.Reviewer.LastVerification, fixture.Goal.EffectiveAcceptanceCriteriaCorrections,
            fixture.Reviewer.LastVerification!.MergedReviewFindings!).DeferToReviewRetryRoute);
        Assert.False(fixture.Decide(out _));

        fixture.AdvanceToDecision();
        var retry = fixture.Retries[1];
        Assert.Equal(fixture.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester).Id, retry.Task);
        Assert.StartsWith("auto-review-retry round ", retry.Feedback);
        Assert.Contains(finding.StableId, retry.Feedback);
        Assert.Equal(2, fixture.ReviewDispatches);
        Assert.Equal(2, fixture.Retries.Count);
        Assert.Single(fixture.Executions);
    }

    [Theory]
    [InlineData("I disagree with the passing receipt.")]
    [InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/PreTesterGuardSelections.cs:34")]
    public void DeliveredGreen_NoRemainingChange_HoldsWithOpenFinding(string description)
    {
        using var fixture = new ReviewerPassingFindingEvidenceFixture();
        var finding = ReviewerPassingFindingEvidenceFixture.CliFinding() with { Description = description };
        fixture.Report([finding]);
        fixture.AdvanceToDecision();
        fixture.Report([finding]);
        var result = fixture.AdvanceToDecision();
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("actionable-change-unavailable", hold.Reason);
        Assert.Contains(finding.StableId, hold.Reason);
        Assert.Contains(Assert.Single(fixture.Receipts).ReceiptId, hold.Reason);
        Assert.NotNull(hold.Decision);
        Assert.Single(fixture.Retries);
        Assert.Single(fixture.Executions);
        Assert.Equal(ReviewFindingState.Open, Assert.Single(fixture.Reviewer.LastVerification!.MergedReviewFindings!).State);
    }

    [Fact]
    public void DeliveredGreen_MissingRegression_RemainsOpenAfterCorrection()
    {
        using var fixture = new ReviewerPassingFindingEvidenceFixture();
        var finding = ReviewerPassingFindingEvidenceFixture.CliFinding() with
        { Category = FindingCategory.TestCoverage, Description = "Missing regression coverage when prior.NotRun contains a CLI-only class; add that scenario." };
        fixture.Report([finding]);
        fixture.AdvanceToDecision();
        fixture.Report([finding]);
        fixture.AdvanceToDecision();
        Assert.Equal(fixture.Developer.Id, fixture.Retries.Last().Task);
        Assert.All(fixture.Reviewer.VerificationHistory.SelectMany(record => record.MergedReviewFindings ?? []),
            item => Assert.Equal(ReviewFindingState.Open, item.State));
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal));
    }
}
