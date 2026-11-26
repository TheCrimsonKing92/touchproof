using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;
using static ConductorDriverTestsGreenTesterReceiptClosure;

// Parallel-safe: each fixture owns its temp directory and kernel; execution uses inline seams.
public sealed class FindingReceiptClosureDiagnosisRoutingTests
{
    private const string CandidateSha = "abc1234abcdef1234abcdef1234abcdef1234abcd12";
    private const string OlderSha = "def5678abcdef1234abcdef1234abcdef1234abcd12";

    private static ReviewFinding Finding(string id = "closure-finding", string description = "Focused evidence missing") =>
        EvidenceFindingWithRequest(description, id, classes: ["ConductorDriverTests"]);

    [Fact]
    public void ReviewedOlderCommitRoutesTesterWithReceiptAndDiagnosis()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([Finding()], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();

        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        Assert.DoesNotContain(fixture.Developer!.Id, fixture.Retried);
        var receipt = Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.True(receipt.Accepted && receipt.Passed);
        Assert.Equal(CandidateSha, receipt.CandidateSha);
        var message = Assert.Single(fixture.RetryMessages);
        Assert.Contains(receipt.ReceiptId, message, StringComparison.Ordinal);
        Assert.Contains(CandidateSha, message, StringComparison.Ordinal);
        Assert.Contains(OlderSha, message, StringComparison.Ordinal);
        Assert.Contains("closure-finding", message, StringComparison.Ordinal);
        Assert.Contains(FindingReceiptClosureDiagnosis.ReviewedCommitDiffers, message, StringComparison.Ordinal);
        Assert.Contains("The supplied receipts passed on the current candidate. Resolve each finding they satisfy, or keep it open and name the source or test location that still needs a Developer change.", message, StringComparison.Ordinal);
        Assert.Equal(0, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
            fixture.Goal.Timeline, fixture.Requester.Id, CandidateSha, "closure-finding"));
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal));
        fixture.AdvanceOnce();
        Assert.Single(fixture.Retried); // Merely observing another tick is not a Tester re-raise.
    }

    [Fact]
    public void AllConditionsHoldClosesByReceiptWithNoWorkerRouted()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([Finding()]);
        fixture.AdvanceToDecision();

        Assert.Empty(fixture.Retried);
        Assert.Empty(fixture.Escalations);
        Assert.Equal(FindingReceiptClosureDiagnosis.Closable, fixture.ReceiptDiagnosis!.Code);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Requester.Status);
        var receipt = Assert.Single(fixture.Requester.LastVerification!.FindingEvidenceReceipts!);
        Assert.Equal(ReviewFindingState.Resolved, Assert.Single(fixture.Requester.LastVerification.MergedReviewFindings!).State);
        Assert.Contains(fixture.Goal.Timeline, item => item.Message ==
            $"FINDING_RECEIPT_CLOSURE task={fixture.Requester.Id.Value[..8]} findings=closure-finding receipts={receipt.ReceiptId} " +
            $"candidate={CandidateSha} candidate_counts={receipt.ReceiptId}:executed=1:passed=1; no worker dispatched");
    }

    [Fact]
    public void OlderHistoryReceiptClosesWithCandidateCountsAndNoWorkerDispatched()
    {
        using var fixture = new ClosureFixture();
        var finding = Finding();
        var receipt = GreenHistoryReceipt(finding);
        receipt = receipt with
        {
            Arms =
            [
                new(FindingEvidenceArm.SourceReverted, CandidateSha, FindingEvidenceArmDisposition.Red,
                    Accepted: true, Passed: false, Summary: "One test failed on reverted source",
                    FailingTestIdentities: ["ConductorDriverTests.DetectsRegression"], ExecutedTestCount: 7),
                .. receipt.Arms!
            ]
        };
        SeedOlderHistoryReceipt(fixture, finding, receipt);
        var timelineCount = fixture.Goal.Timeline.Count;
        var lastDispatch = fixture.Requester.LastDispatch;
        var closedAt = fixture.Requester.LastVerification!.CompletedAt.AddSeconds(1);

        Assert.True(FindingReceiptClosureCoordinator.TryClose(fixture.Kernel, fixture.Goal, fixture.Requester.Id,
            CandidateSha, () => closedAt, out var diagnosis, out _), diagnosis.Describe());

        Assert.Equal(FindingReceiptClosureDiagnosis.Closable, diagnosis.Code);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Requester.Status);
        var closed = Assert.Single(fixture.Requester.LastVerification!.MergedReviewFindings!);
        Assert.Equal(finding.StableId, closed.StableId);
        Assert.Equal(ReviewFindingState.Resolved, closed.State);
        var request = ConductorDriver.BuildFindingEvidenceIdentity(receipt.Request);
        Assert.Equal($"Closed by conductor receipt closure: receipt {receipt.ReceiptId}; request {request}; " +
            $"candidate {CandidateSha}; Candidate counts (executed=3, passed=3).", closed.Description);
        var newEvents = fixture.Goal.Timeline.Skip(timelineCount).ToArray();
        var closure = Assert.Single(newEvents, item => item.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal));
        Assert.Equal($"FINDING_RECEIPT_CLOSURE task={fixture.Requester.Id.Value[..8]} findings={finding.StableId} " +
            $"receipts={receipt.ReceiptId} candidate={CandidateSha} candidate_counts={receipt.ReceiptId}:executed=3:passed=3; no worker dispatched",
            closure.Message);
        Assert.Same(lastDispatch, fixture.Requester.LastDispatch);
        Assert.DoesNotContain(newEvents, item => item.Kind is ProgressKind.TaskDispatchRecorded or
            ProgressKind.TaskProcessStarted or ProgressKind.TaskRetried or ProgressKind.HumanInputRequested);
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.Retried);
        Assert.Empty(fixture.Escalations);
    }

    [Fact]
    public void OlderHistoryReceiptAtDifferentCandidateLeavesFindingOpen()
    {
        var finding = Finding();
        AssertOlderHistoryReceiptRejected(finding, GreenHistoryReceipt(finding, OlderSha),
            FindingReceiptClosureDiagnosis.ReceiptCandidateDiffers);
    }

    [Fact]
    public void OlderHistoryNonPassingReceiptLeavesFindingOpen()
    {
        var finding = Finding();
        var receipt = GreenHistoryReceipt(finding) with
        {
            Passed = false,
            Summary = "Candidate tests failed",
            Arms =
            [
                new(FindingEvidenceArm.Candidate, CandidateSha, FindingEvidenceArmDisposition.Red,
                    Accepted: true, Passed: false, Summary: "One Candidate test failed",
                    FailingTestIdentities: ["ConductorDriverTests.DetectsRegression"], ExecutedTestCount: 3)
            ]
        };
        AssertOlderHistoryReceiptRejected(finding, receipt, FindingReceiptClosureDiagnosis.ReceiptNotPassing);
    }

    [Fact]
    public void OlderHistoryReceiptForNonTestEvidenceFindingLeavesFindingOpen()
    {
        var finding = Finding() with { Category = FindingCategory.Correctness };
        AssertOlderHistoryReceiptRejected(finding, GreenHistoryReceipt(finding),
            FindingReceiptClosureDiagnosis.FindingCategoryNotTestEvidence);
    }

    [Fact]
    public void OlderHistoryReceiptWithoutRequestedNegativeControlLeavesFindingOpen()
    {
        var finding = Finding();
        finding = finding with
        {
            EvidenceRequest = finding.EvidenceRequest! with { NegativeControl = FindingEvidenceNegativeControl.RevertSrc }
        };
        AssertOlderHistoryReceiptRejected(finding, GreenHistoryReceipt(finding),
            FindingReceiptClosureDiagnosis.NegativeControlNotDemonstrated);
    }

    private static FindingEvidenceReceipt GreenHistoryReceipt(ReviewFinding finding, string candidateSha = CandidateSha) =>
        new("historical-receipt", candidateSha, finding.EvidenceRequest!, Accepted: true, Passed: true,
            Summary: "Candidate tests passed",
            Arms:
            [
                new(FindingEvidenceArm.Candidate, candidateSha, FindingEvidenceArmDisposition.Green,
                    Accepted: true, Passed: true, Summary: "Three Candidate tests passed", ExecutedTestCount: 3)
            ]);

    private static void SeedOlderHistoryReceipt(ClosureFixture fixture, ReviewFinding finding, FindingEvidenceReceipt receipt)
    {
        var requester = fixture.Requester;
        var outcome = new FindingEvidenceOutcome(true, receipt.ReceiptId, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence,
            RequestedSelectionIdentity: ConductorDriver.BuildFindingEvidenceIdentity(receipt.Request));
        fixture.Report([finding]);
        fixture.Kernel.RecordFindingEvidenceOutcome(fixture.Goal.Id, requester.Id, finding.StableId, outcome, receipt);
        fixture.Kernel.RetryTask(fixture.Goal.Id, requester.Id, "Ask Tester to reconsider the finding", RetryCause.NewTestFinding);

        fixture.Report([finding]);
        fixture.Kernel.RecordFindingEvidenceOutcome(fixture.Goal.Id, requester.Id, finding.StableId, outcome);

        var latest = requester.LastVerification!;
        Assert.Equal(CandidateSha, latest.ReviewedCommit);
        Assert.Equal(receipt.ReceiptId, Assert.Single(latest.MergedReviewFindings!).EvidenceOutcome!.ReceiptId);
        Assert.Empty(latest.FindingEvidenceReceipts ?? []);
        Assert.Contains(requester.VerificationHistory.Take(requester.VerificationHistory.Count - 1),
            record => record.FindingEvidenceReceipts?.Contains(receipt) == true);
    }

    private static void AssertOlderHistoryReceiptRejected(ReviewFinding finding, FindingEvidenceReceipt receipt, string expectedDiagnosis)
    {
        using var fixture = new ClosureFixture();
        SeedOlderHistoryReceipt(fixture, finding, receipt);

        Assert.False(FindingReceiptClosureCoordinator.TryClose(fixture.Kernel, fixture.Goal, fixture.Requester.Id,
            CandidateSha, () => DateTimeOffset.UtcNow, out var diagnosis, out _));

        Assert.Equal(expectedDiagnosis, diagnosis.Code);
        Assert.Equal(ReviewFindingState.Open, Assert.Single(fixture.Requester.LastVerification!.MergedReviewFindings!).State);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Requester.Status);
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal));
    }

    [Fact]
    public void RequestedNegativeControlCannotCloseWithCandidateOnlyReceipt()
    {
        using var fixture = new ClosureFixture();
        var finding = Finding();
        finding = finding with
        {
            EvidenceRequest = finding.EvidenceRequest! with { NegativeControl = FindingEvidenceNegativeControl.RevertSrc }
        };
        fixture.Report([finding]);
        fixture.AdvanceToDecision();

        Assert.Equal(FindingReceiptClosureDiagnosis.NegativeControlNotDemonstrated, fixture.ReceiptDiagnosis!.Code);
        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        Assert.DoesNotContain(fixture.Developer!.Id, fixture.Retried);
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Message.StartsWith("FINDING_RECEIPT_CLOSURE", StringComparison.Ordinal));
    }

    [Fact]
    public void TesterReRaiseAtSameCandidateRoutesDeveloperWithReasonAndDiagnosis()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([Finding()], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();
        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        var receipt = Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!);

        const string reason = "src/Synthetic.cs:42 still discards the result";
        fixture.Report([Finding(description: reason)], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();

        Assert.Equal([fixture.Requester.Id, fixture.Developer!.Id], fixture.Retried);
        Assert.Equal(RetryCause.NewSourceFinding, fixture.RetryCauses.Last());
        var message = fixture.RetryMessages.Last();
        Assert.Contains("route=developer", message, StringComparison.Ordinal);
        Assert.Contains(reason, message, StringComparison.Ordinal);
        Assert.Contains(receipt.ReceiptId, message, StringComparison.Ordinal);
        Assert.Contains(CandidateSha, message, StringComparison.Ordinal);
        Assert.Contains(FindingReceiptClosureDiagnosis.ReviewedCommitDiffers, message, StringComparison.Ordinal);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public void NewlyRaisedFindingGetsTesterReceiptDeliveryBeforeDeveloperRoute()
    {
        using var fixture = new ClosureFixture();
        fixture.Report([Finding()], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();
        fixture.Report([Finding(), Finding("new-finding")], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();

        Assert.Equal([fixture.Requester.Id, fixture.Requester.Id], fixture.Retried);
        Assert.DoesNotContain(fixture.Developer!.Id, fixture.Retried);
        Assert.Contains("new-finding", fixture.RetryMessages.Last(), StringComparison.Ordinal);
        fixture.Report([Finding(), Finding("new-finding")], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();
        Assert.Equal(fixture.Developer.Id, fixture.Retried.Last());
    }

    [Fact]
    public void TesterReRaiseWithoutDeveloperEscalatesWithDiagnosis()
    {
        using var fixture = new ClosureFixture(includeDeveloper: false);
        fixture.Report([Finding()], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();
        Assert.Equal([fixture.Requester.Id], fixture.Retried);
        Assert.Empty(fixture.Escalations);
        fixture.Report([Finding(description: "tests/SyntheticTests.cs:17 requires repair")], reviewedSha: OlderSha);
        fixture.AdvanceToDecision();

        var message = Assert.Single(fixture.Escalations);
        Assert.Contains("No upstream Developer", message, StringComparison.Ordinal);
        Assert.Contains(FindingReceiptClosureDiagnosis.ReviewedCommitDiffers, message, StringComparison.Ordinal);
        Assert.Contains("tests/SyntheticTests.cs:17 requires repair", message, StringComparison.Ordinal);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public void FailingReceiptKeepsActionableRedDeveloperRouteAndMessage()
    {
        // Contrast the two evidence verdicts at the same seam: only RED routes Developer immediately.
        using var passing = new ClosureFixture();
        passing.Report([Finding()], reviewedSha: OlderSha);
        passing.AdvanceToDecision();
        Assert.Equal([passing.Requester.Id], passing.Retried);

        using var fixture = new ClosureFixture(evidenceFailure: "candidate-red");
        fixture.Report([Finding()]);
        fixture.AdvanceToDecision();

        Assert.Equal([fixture.Developer!.Id], fixture.Retried);
        var receipt = Assert.Single(fixture.Requester.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.False(receipt.Passed);
        var candidate = receipt.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var failingTests = candidate.FailingTestIdentities!.Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(failingTests);
        var message = Assert.Single(fixture.RetryMessages);
        Assert.StartsWith($"ACTIONABLE_CANDIDATE_RED candidate_sha={CandidateSha}; receipt_id={receipt.ReceiptId}; " +
            $"finding_ids=closure-finding; failing_tests={string.Join(',', failingTests)}.  " +
            "Repair the Developer-owned source/test anchor before any remaining focused evidence or downstream verification runs.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnosis=", message, StringComparison.Ordinal);
        Assert.DoesNotContain("route=", message, StringComparison.Ordinal);
    }
}
