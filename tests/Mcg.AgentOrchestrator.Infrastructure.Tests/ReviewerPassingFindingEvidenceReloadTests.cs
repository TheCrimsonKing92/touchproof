using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: in-memory persistence round trips and uniquely owned artifacts, no real workers.
public sealed class ReviewerPassingFindingEvidenceReloadTests
{
    [Fact]
    public void ConsumedFindings_CorrectedPeer_PreservesOtherCorrection()
    {
        using var fixture = Delivered();
        var first = ReviewerPassingFindingEvidenceFixture.CliFinding();
        var second = ReviewerPassingFindingEvidenceFixture.CaptureFinding() with { EvidenceRequest = first.EvidenceRequest };
        var delivery = Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver.BuildCappedFindingEvidenceDeliveryRetry(
            fixture.Goal, fixture.Reviewer, fixture.Candidate, [second], [Assert.Single(fixture.Receipts).ReceiptId],
            "Independent finding requests already covered evidence.");
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, delivery.Evidence,
            retryRoundKind: RetryRoundKind.Mechanical, retryCause: RetryCause.CriterionEvidenceOwnerMismatch);
        fixture.Report([first, second]);
        var review = fixture.Reviewer.LastVerification!;
        Assert.Equal(2, review.MergedReviewFindings!.Count);
        // A current partial ledger can route A while B remains in retained completed-review proof.
        fixture.ReattachVerification(review with { MergedReviewFindings = [review.MergedReviewFindings.Single(item => item.StableId == first.StableId)] });
        Assert.True(fixture.Decide(out var firstCorrection));
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Developer.Id, firstCorrection.Evidence, retryCause: RetryCause.NewSourceFinding);
        foreach (var predecessor in fixture.Goal.Tasks.TakeWhile(item => item.Id != fixture.Reviewer.Id))
            if (predecessor.Status != WorkTaskStatus.Completed)
                ConductorDriverTests.PassVerification(fixture.Kernel, fixture.Goal, predecessor);
        fixture.ReattachVerification(review);
        fixture.Reload();
        Assert.True(fixture.Decide(out var next));
        Assert.Equal(RetryCause.NewSourceFinding, next.ObservedCause);
        Assert.Equal(fixture.Developer.Id, next.TargetTaskId);
        Assert.Contains("routed_finding_ids=" + second.StableId + ";", next.Evidence);
        Assert.Contains("held_finding_ids=" + first.StableId + ":correction-already-routed", next.Evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedContinuation_CompletedReview_RequiresFindingAssociation(bool foreign)
    {
        using var fixture = Delivered();
        var receipt = Assert.Single(fixture.Receipts);
        var source = fixture.Reviewer.VerificationHistory.Last();
        var dispatchAt = source.DispatchAttemptAt!.Value;
        var finding = ReviewerPassingFindingEvidenceFixture.CliFinding();
        var selection = new FindingEvidenceReviewSelection(foreign ? "foreign-finding" : finding.StableId,
            Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver.BuildFindingEvidenceIdentity(receipt.Request),
            fixture.Reviewer.Id.Value, source.CompletedAt, dispatchAt, receipt.FindingRoundFingerprint!,
            receipt.ReceiptId, FindingEvidenceReviewContext.ExecutionIdentity(receipt));
        fixture.Kernel.RecordFindingEvidenceReviewContinuation(fixture.Goal.Id, fixture.Reviewer.Id,
            new(fixture.Goal.Id.Value, fixture.Reviewer.Id.Value, fixture.Candidate, new("patch", "base", "manifest"),
                source.CompletedAt, dispatchAt, [], [selection]));
        fixture.PruneLegacyDeliveryMarkers();
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Message.StartsWith("finding evidence-on-demand:", StringComparison.Ordinal));
        fixture.Report([finding]);
        fixture.Reload();
        Assert.Equal(!foreign, fixture.Decide(out var correction));
        if (!foreign)
        {
            Assert.Equal(RetryCause.NewSourceFinding, correction.ObservedCause);
            Assert.Equal(fixture.Developer.Id, correction.TargetTaskId);
            Assert.Contains(receipt.ReceiptId, correction.Evidence);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedReview_Reload_RetainsCorrectionProof(bool legacyContext)
    {
        using var fixture = Delivered();
        fixture.Report([ReviewerPassingFindingEvidenceFixture.CliFinding()], legacyContext: legacyContext);
        Assert.True(fixture.Decide(out var before));
        Assert.Equal(RetryCause.NewSourceFinding, before.ObservedCause);
        fixture.Reload();
        Assert.True(fixture.Decide(out var after));
        Assert.Equal(before, after);
        fixture.AdvanceToDecision();
        Assert.Equal(fixture.Developer.Id, fixture.Retries.Last().Task);
        Assert.Equal(2, fixture.ReviewDispatches);
        Assert.Single(fixture.Executions);
        // Repeated conductor ticks/reloads cannot spend a second correction on this episode.
        fixture.Reload();
        fixture.Advance();
        fixture.Advance();
        Assert.Single(fixture.Retries.Where(item => item.Task == fixture.Developer.Id));
    }

    [Fact]
    public void ReceiptReattachment_WithoutCompletedReview_DoesNotConsumeDelivery()
    {
        using var fixture = Delivered();
        var receipt = Assert.Single(fixture.Receipts);
        var finding = ReviewerPassingFindingEvidenceFixture.CliFinding();
        fixture.ReattachVerification(fixture.Reviewer.VerificationHistory.Last());
        fixture.Kernel.RecordFindingEvidenceOutcome(fixture.Goal.Id, fixture.Reviewer.Id, finding.StableId,
            new(Honoured: true, ReceiptId: receipt.ReceiptId, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence), receipt);
        fixture.Reload();
        Assert.False(fixture.Decide(out _));
        Assert.Equal(1, fixture.ReviewDispatches);
        Assert.True(fixture.Reviewer.LastVerification!.Succeeded);
        // Reattachment records passing verification. Resume through the normal retry
        // transition before starting the still-unconsumed review; keep its history.
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id,
            "Resume evidence review after receipt reattachment without a completed model review.",
            retryRoundKind: RetryRoundKind.Mechanical, retryCause: RetryCause.CriterionEvidenceOwnerMismatch);
        fixture.Report([finding], completed: false);
        Assert.False(fixture.Decide(out _));
        Assert.Equal(2, fixture.ReviewDispatches);
        Assert.Single(fixture.Executions);
        Assert.DoesNotContain(fixture.Retries, item => item.Task == fixture.Developer.Id);
    }

    [Fact]
    public void InterruptedReview_WithoutParsedCompletion_DoesNotConsumeDelivery()
    {
        using var fixture = Delivered();
        fixture.Report([ReviewerPassingFindingEvidenceFixture.CliFinding()], interrupted: true);
        fixture.Reload();
        Assert.False(fixture.Decide(out _));
        Assert.DoesNotContain(fixture.Retries, item => item.Task == fixture.Developer.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsumedReview_ExhaustedBudgetOrMissingDeveloper_Holds(bool missingDeveloper)
    {
        using var fixture = Delivered();
        fixture.Report([ReviewerPassingFindingEvidenceFixture.CliFinding()]);
        var cap = missingDeveloper ? new ReviewRetryCapReceipt(1, 4) : new ReviewRetryCapReceipt(4, 4);
        Assert.True(fixture.Decide(out var hold, cap, includeDeveloper: !missingDeveloper));
        Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, hold.Kind);
        Assert.Null(hold.TargetTaskId);
        Assert.Contains(missingDeveloper ? "developer-target-unavailable" : "correction-budget-exhausted", hold.Evidence);
        Assert.Contains(fixture.Candidate, hold.Evidence);
        fixture.Reload();
        Assert.True(fixture.Decide(out var reloaded, cap, includeDeveloper: !missingDeveloper));
        Assert.Equal(hold, reloaded);
        Assert.Single(fixture.Retries);
    }

    [Fact]
    public void ConsumedReview_CorrectionRecorded_HoldsAfterReload()
    {
        using var fixture = Delivered();
        fixture.Report([ReviewerPassingFindingEvidenceFixture.CliFinding()]);
        Assert.True(fixture.Decide(out var correction));
        var review = fixture.Reviewer.LastVerification!;
        // Record the same effect the recovery executor applies, then replay retained history.
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Developer.Id, correction.Evidence,
            retryCause: RetryCause.NewSourceFinding);
        // Reattachment/bookkeeping may restore the retained verdict; it is not another dispatch.
        fixture.ReattachVerification(review);
        fixture.Reload();
        Assert.True(fixture.Decide(out var hold));
        Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, hold.Kind);
        Assert.Contains("correction-already-routed", hold.Evidence);
        Assert.Equal(1, fixture.Goal.Timeline.Count(item => item.Kind == ProgressKind.TaskRetried &&
            item.TaskId == fixture.Developer.Id && item.Message.Contains("REVIEWER_PASSING_EVIDENCE_CORRECTION", StringComparison.Ordinal)));
    }

    [Fact]
    public void CompletedReview_ChangedCandidate_DoesNotSuppressNewEvidence()
    {
        using var fixture = Delivered();
        fixture.Report([ReviewerPassingFindingEvidenceFixture.CliFinding()]);
        fixture.Candidate = "7b50a02394baa03c45d5f557e6a3f8df21f0e530";
        Assert.False(fixture.Decide(out _));
        fixture.AdvanceToDecision();
        Assert.Equal(2, fixture.Executions.Count);
        Assert.Contains(fixture.Receipts, receipt => receipt.CandidateSha == fixture.Candidate);
    }

    [Fact]
    public void CompletedReview_ExpandedRequest_RunsNewSelection()
    {
        using var fixture = Delivered(FindingCategory.TestEvidence);
        var finding = ReviewerPassingFindingEvidenceFixture.CliFinding() with { Category = FindingCategory.TestEvidence };
        finding = finding with { EvidenceRequest = new([.. finding.EvidenceRequest!.Selections,
            new("Infrastructure.Tests", "JunitCommandEnvironmentDiagnosticTests")]) };
        fixture.Report([finding]);
        Assert.False(fixture.Decide(out _));
        fixture.AdvanceToDecision();
        Assert.Equal(2, fixture.Executions.Count);
        Assert.Contains("JunitCommandEnvironmentDiagnosticTests", fixture.Executions[1]);
        Assert.DoesNotContain(fixture.Retries, item => item.Task == fixture.Developer.Id);
    }

    [Fact]
    public void DeliveredEvidence_NewFindingWithSameRequest_HasOwnReviewEntitlement()
    {
        using var fixture = Delivered();
        var first = ReviewerPassingFindingEvidenceFixture.CliFinding();
        fixture.Report([first with { State = ReviewFindingState.Resolved },
            first with { StableId = "different-finding", Category = FindingCategory.TestEvidence,
                Location = ReviewerPassingFindingEvidenceFixture.CaptureFinding().Location }]);
        Assert.False(fixture.Decide(out _));
        fixture.AdvanceToDecision();
        Assert.Single(fixture.Executions);
        Assert.All(fixture.Retries, item => Assert.Equal(fixture.Reviewer.Id, item.Task));
    }

    [Fact]
    public void MultipleFindings_ConsumedOne_DoesNotClaimUnrelatedDelivery()
    {
        using var fixture = Delivered(FindingCategory.TestEvidence);
        var first = ReviewerPassingFindingEvidenceFixture.CliFinding() with { Category = FindingCategory.TestEvidence };
        var second = ReviewerPassingFindingEvidenceFixture.CaptureFinding() with { Category = FindingCategory.TestEvidence };
        fixture.Report([first, second]);
        Assert.False(fixture.Decide(out _)); // second request still needs execution
        fixture.AdvanceToDecision();
        Assert.Equal(2, fixture.Executions.Count);
        Assert.DoesNotContain(fixture.Retries, item => item.Task == fixture.Developer.Id);
        Assert.Equal(2, fixture.Retries.Count);
        Assert.Equal(1, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
            fixture.Goal.Timeline, fixture.Reviewer.Id, fixture.Candidate, first.StableId));
        Assert.Equal(1, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
            fixture.Goal.Timeline, fixture.Reviewer.Id, fixture.Candidate, second.StableId));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("foreign")]
    [InlineData("negative")]
    public void CompletedReview_UnqualifiedReceipt_DoesNotAuthorizeCorrection(string kind)
    {
        using var fixture = Delivered();
        fixture.Report([ReviewerPassingFindingEvidenceFixture.CliFinding()]);
        // The identical selector must authorize correction before receipt qualification is broken.
        Assert.True(Decide(fixture.Kernel, out var qualified));
        Assert.Equal(fixture.Developer.Id, qualified.TargetTaskId);
        Assert.Contains("REVIEWER_PASSING_EVIDENCE_CORRECTION", qualified.Evidence);
        var snapshot = fixture.Kernel.ExportSnapshot();
        // Mutate every retained copy, including the origin, so no valid receipt remains to select.
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        var receipt = Assert.Single(fixture.Receipts);
        var replacement = kind switch
        {
            "stale" => receipt with { CandidateSha = new string('a', 40) },
            "foreign" => receipt with { ExecutionBasisIdentity = "foreign-executor" },
            _ => receipt with { Passed = false }
        };
        json = json.Replace(System.Text.Json.JsonSerializer.Serialize(receipt),
            System.Text.Json.JsonSerializer.Serialize(replacement), StringComparison.Ordinal);
        var kernel = AgentOrchestratorKernel.FromSnapshot(System.Text.Json.JsonSerializer.Deserialize<OrchestratorSnapshot>(json)!);
        var task = kernel.GetTask(fixture.Goal.Id, fixture.Reviewer.Id);
        var copies = task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []).ToArray();
        Assert.NotEmpty(copies);
        Assert.All(copies, item =>
        {
            Assert.Equal(replacement.CandidateSha, item.CandidateSha);
            Assert.Equal(replacement.ExecutionBasisIdentity, item.ExecutionBasisIdentity);
            Assert.Equal(replacement.Passed, item.Passed);
        });
        Assert.False(Decide(kernel, out _));

        bool Decide(AgentOrchestratorKernel source, out FailedGoalFindingObservation observation) =>
            Mcg.AgentOrchestrator.App.Orchestration.ReviewerPassingFindingEvidenceRouter.TryDecide(
                source.GetGoal(fixture.Goal.Id), source.GetTask(fixture.Goal.Id, fixture.Reviewer.Id),
                source.GetTask(fixture.Goal.Id, fixture.Developer.Id), fixture.Candidate,
                ReviewerPassingFindingEvidenceFixture.NormalizeRequest,
                Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(
                    new Mcg.AgentOrchestrator.Infrastructure.AcceptanceGateEngineSettings()), new(1, 4), out observation);
    }

    private static ReviewerPassingFindingEvidenceFixture Delivered(FindingCategory category = FindingCategory.Correctness,
        ReviewFinding? finding = null)
    {
        var fixture = new ReviewerPassingFindingEvidenceFixture();
        fixture.Report([finding ?? ReviewerPassingFindingEvidenceFixture.CliFinding() with { Category = category }]);
        fixture.AdvanceToDecision();
        Assert.Equal(fixture.Reviewer.Id, Assert.Single(fixture.Retries).Task);
        Assert.Single(fixture.Executions);
        Assert.Single(fixture.Receipts);
        return fixture;
    }

    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void MissingActionableTarget_ReloadAndTicks_KeepTypedHold()
    {
        using var fixture = Delivered();
        fixture.Report([ReviewerPassingFindingEvidenceFixture.CliFinding()]);
        var retained = Assert.Single(fixture.Reviewer.LastVerification!.MergedReviewFindings!);
        var blank = retained with { Location = retained.Location with { Region = "" } };
        // Keep the parsed Reviewer output valid while restoring a structurally incomplete ledger.
        var json = JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot());
        Assert.Contains(JsonSerializer.Serialize(retained), json);
        json = json.Replace(JsonSerializer.Serialize(retained), JsonSerializer.Serialize(blank), StringComparison.Ordinal);
        var kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(json)!);
        var tick = 0;
        var driver = CreateDriver();
        AssertRetainedFinding();

        var result = Advance();
        for (var step = 0; step < 8 && result.Outcome is not ConductorAdvanceOutcome.Held; step++)
            result = Advance();
        var before = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("actionable-change-unavailable", before.Reason);
        Assert.Contains(retained.StableId, before.Reason);
        Assert.Contains(Assert.Single(fixture.Receipts).ReceiptId, before.Reason);
        Assert.NotNull(before.Decision);
        Assert.Equal("Hold", before.Decision.Action);

        kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
        driver = CreateDriver();
        AssertRetainedFinding();
        for (var step = 0; step < 3; step++)
        {
            var after = Assert.IsType<ConductorAdvanceOutcome.Held>(Advance().Outcome);
            Assert.Equal(before.Reason, after.Reason);
            Assert.NotNull(after.Decision);
            Assert.Equal("Hold", after.Decision.Action);
            Assert.Equal(before.Decision.DiscriminatingEvidence, after.Decision.DiscriminatingEvidence);
            AssertRetainedFinding();
        }
        Assert.Single(fixture.Retries);
        Assert.Single(fixture.Executions);
        Assert.Equal(2, kernel.GetTask(fixture.Goal.Id, fixture.Reviewer.Id).DispatchHistory.Count);

        ConductorDriver CreateDriver() => ConductorDriverTests.MakeDriver(
            getPreReviewEvidenceContext: _ => ConductorDriverTests.NoPreReviewContext(fixture.Candidate),
            resolveFindingEvidenceSiblingClasses: (_, _, testClass) => [testClass],
            runFocusedEvidence: (_, _) => throw new InvalidOperationException("Delivered evidence must not be executed again."),
            retryTaskWithCause: (goal, task, message, kind, cause) =>
            {
                fixture.Retries.Add((task, cause, message));
                return kernel.RetryTask(goal, task, message, retryRoundKind: kind, retryCause: cause);
            });

        ConductorAdvanceResult Advance()
        {
            driver.BeginTick(kernel, ++tick);
            return driver.AdvanceOnce(kernel.GetGoal(fixture.Goal.Id), ConductorAutonomyPolicy.Permissive);
        }

        void AssertRetainedFinding()
        {
            var review = kernel.GetTask(fixture.Goal.Id, fixture.Reviewer.Id).LastVerification!;
            var finding = Assert.Single(review.MergedReviewFindings!);
            Assert.Equal(retained.StableId, finding.StableId);
            Assert.Equal("", finding.Location.Region);
            Assert.Equal(ReviewFindingState.Open, finding.State);
            Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(review, out var round, out _));
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(round.Findings).Location.Region));
        }
    }

    [Theory(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    [InlineData("docs/operator-runbook.md", FindingCategory.Correctness)]
    [InlineData("src/Mcg.AgentOrchestrator.App/Orchestration/PreTesterGuardSelections.cs", FindingCategory.TestEvidence)]
    public void IneligibleStructure_ReloadAndTicks_KeepTypedHold(string file, FindingCategory category)
    {
        var finding = ReviewerPassingFindingEvidenceFixture.CliFinding() with
        { Location = new(file, "34-37"), Category = category };
        using var fixture = Delivered(finding: finding);
        fixture.Report([finding]);
        Assert.True(fixture.Decide(out var observation));
        Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, observation.Kind);
        Assert.Null(observation.TargetTaskId);
        var before = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.AdvanceToDecision().Outcome);
        Assert.Contains("actionable-change-unavailable", before.Reason);
        Assert.Contains(finding.StableId, before.Reason);
        Assert.Contains(Assert.Single(fixture.Receipts).ReceiptId, before.Reason);
        Assert.NotNull(before.Decision);
        Assert.Equal("Hold", before.Decision.Action);
        fixture.Reload();
        for (var tick = 0; tick < 3; tick++)
        {
            var after = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
            Assert.Equal(before.Reason, after.Reason);
            Assert.NotNull(after.Decision);
            Assert.Equal("Hold", after.Decision.Action);
            Assert.Equal(before.Decision!.DiscriminatingEvidence, after.Decision.DiscriminatingEvidence);
            Assert.Equal(ReviewFindingState.Open, Assert.Single(fixture.Reviewer.LastVerification!.MergedReviewFindings!).State);
        }
        Assert.Single(fixture.Retries);
        Assert.Single(fixture.Executions);
        Assert.Equal(2, fixture.ReviewDispatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalDeliveryCap_WithOrWithoutReviewProof_Holds(bool completedReview)
    {
        using var fixture = Delivered();
        var finding = ReviewerPassingFindingEvidenceFixture.CliFinding();
        if (completedReview) fixture.Report([finding]);
        var review = fixture.Reviewer.LastVerification ?? fixture.Reviewer.VerificationHistory.Last();
        var delivery = fixture.Retries.Single().Feedback;
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, delivery,
            retryRoundKind: RetryRoundKind.Mechanical, retryCause: RetryCause.CriterionEvidenceOwnerMismatch);
        fixture.ReattachVerification(review);
        fixture.Reload();
        Assert.True(fixture.Decide(out var hold));
        Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, hold.Kind);
        Assert.Contains("delivery-budget-exhausted", hold.Evidence);
        Assert.Contains(finding.StableId, hold.Evidence);
        if (!completedReview) Assert.Contains("review_completed=unproven", hold.Evidence);
        Assert.Single(fixture.Executions);
    }

    [Fact]
    public void FirstDelivery_ReviewBudgetExhausted_HoldsWithoutFreshReview()
    {
        using var fixture = new ReviewerPassingFindingEvidenceFixture();
        fixture.Report([ReviewerPassingFindingEvidenceFixture.CliFinding()]);
        var policy = ConductorAutonomyPolicy.Permissive with { ReviewAutoRetryStopRound = 1 };
        var hold = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.AdvanceToDecision(policy).Outcome);
        Assert.Contains("correction-budget-exhausted", hold.Reason);
        Assert.Contains("review_completed=unproven", hold.Reason);
        Assert.Empty(fixture.Retries);
        Assert.Single(fixture.Executions);
        Assert.Equal(1, fixture.ReviewDispatches);
    }
}
