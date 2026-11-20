using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

// Parallel-safe: each case owns its in-memory kernel and clock; driver I/O uses injected callbacks.
public sealed class ReviewerAdvisoryAnchorMoveRoundSalvageTests
{
    private const string BlockerId = "reviewer-fresh-delivery-skips-reattachment";
    private const string AdvisoryId = "reviewer-correction-rework-unclassified";
    private const string PriorFindings = """
        [{"stable_id":"reviewer-fresh-delivery-skips-reattachment","state":"open","severity":"advisory","category":"correctness","location":{"file":"ReviewerPassingFindingEvidenceRouter.cs","region":"87-95"},"description":"Reattach fresh delivery."},{"stable_id":"reviewer-correction-rework-unclassified","state":"open","severity":"advisory","category":"correctness","location":{"file":"ReviewerPassingFindingEvidenceRouter.cs","region":"120"},"description":"Classify correction rework."}]
        """;
    private const string CurrentFindings = """
        [{"stable_id":"reviewer-fresh-delivery-skips-reattachment","state":"open","severity":"blocking","category":"correctness","location":{"file":"ReviewerPassingFindingEvidenceRouter.cs","region":"87-95"},"description":"Reattach fresh delivery."},{"stable_id":"reviewer-correction-rework-unclassified","state":"open","severity":"advisory","category":"correctness","location":{"file":"ReviewerPassingFindingEvidenceRouter.cs","region":"125"},"description":"Unchanged: classify correction rework."}]
        """;
    private const string UnprovenResolutionFindings = """
        [{"stable_id":"reviewer-fresh-delivery-skips-reattachment","state":"resolved","severity":"blocking","category":"correctness","location":{"file":"ReviewerPassingFindingEvidenceRouter.cs","region":"87-95"},"description":"Claimed resolved without proof."},{"stable_id":"reviewer-correction-rework-unclassified","state":"open","severity":"advisory","category":"correctness","location":{"file":"ReviewerPassingFindingEvidenceRouter.cs","region":"125"},"description":"Unchanged: classify correction rework."},{"stable_id":"new-blocker","state":"open","severity":"blocking","category":"correctness","location":{"file":"Other.cs","region":"10"},"description":"New independent blocker."}]
        """;

    [Xunit.Theory]
    [Xunit.InlineData(null, null, false, "pass")]
    [Xunit.InlineData(null, null, true, "pass")]
    [Xunit.InlineData(null, "current-commit", false, "pass")]
    [Xunit.InlineData(null, "current-commit", true, "pass")]
    [Xunit.InlineData("prior-commit", null, false, "pass")]
    [Xunit.InlineData("prior-commit", null, true, "pass")]
    [Xunit.InlineData("prior-commit", "current-commit", false, "pass")]
    [Xunit.InlineData("prior-commit", "current-commit", true, "pass")]
    [Xunit.InlineData(null, null, false, "needs-work")]
    [Xunit.InlineData(null, null, true, "needs-work")]
    [Xunit.InlineData(null, "current-commit", false, "needs-work")]
    [Xunit.InlineData(null, "current-commit", true, "needs-work")]
    [Xunit.InlineData("prior-commit", null, false, "needs-work")]
    [Xunit.InlineData("prior-commit", null, true, "needs-work")]
    [Xunit.InlineData("prior-commit", "current-commit", false, "needs-work")]
    [Xunit.InlineData("prior-commit", "current-commit", true, "needs-work")]
    public void IdentitySalvageWithoutProofKeepsBlockerAcrossReplay(
        string? priorCommit, string? currentCommit, bool atCap, string verdict)
    {
        var fixture = new Fixture();
        fixture.Record(CurrentFindings.Replace("125", "120"), "needs-work", priorCommit);
        Assert.Equal(ReviewFindingState.Open, fixture.Reviewer.LastVerification!.MergedReviewFindings!
            .Single(finding => finding.StableId == BlockerId).State);
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, "Verify claimed resolution");
        fixture.Record(UnprovenResolutionFindings, verdict, currentCommit,
            atCap ? new ReviewRetryCapReceipt(7, 7) : null);

        var verification = fixture.Reviewer.LastVerification!;
        var expectedCode = atCap
            ? ReviewFindingConvergence.UnprovenResolutionAtCapViolationCode
            : ReviewFindingConvergence.MissingReviewRetryCapReceiptViolationCode;
        Assert.Equal(expectedCode, verification.ReviewFindingContractViolation?.Code);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Reviewer.Status);
        Assert.Contains(verification.ReviewFindingContractViolation!.IdentityMismatches!, mismatch =>
            mismatch.SubmittedStableId == AdvisoryId &&
            mismatch.Code == ReviewFindingConvergence.IdentityMovedViolationCode);
        AssertUnprovenResolutionLedger(verification.MergedReviewFindings);
        Assert.Contains(fixture.Goal.Timeline, evt => evt.TaskId == fixture.Reviewer.Id &&
            evt.Kind == ProgressKind.ReviewFindingContractViolationRecorded &&
            evt.Message.Contains(expectedCode, StringComparison.Ordinal));
        var brief = fixture.BuildBrief();
        Assert.Contains(BlockerId, brief, StringComparison.Ordinal);
        Assert.Contains("new-blocker", brief, StringComparison.Ordinal);

        AssertUnprovenResolutionLedger(fixture.Kernel.GetReviewFindingState(fixture.Goal.Id));
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, "Recheck retained blockers");
        fixture.Record(UnprovenResolutionFindings.Replace("\"resolved\"", "\"open\"")
            .Replace("125", "120"), "needs-work", currentCommit);
        Assert.Null(fixture.Reviewer.LastVerification!.ReviewFindingContractViolation);
        AssertUnprovenResolutionLedger(fixture.Reviewer.LastVerification.MergedReviewFindings);
        AssertNoFalseNoBlockerFailure(fixture.Goal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, null)]
    [Xunit.InlineData(null, "current-commit")]
    [Xunit.InlineData("prior-commit", null)]
    [Xunit.InlineData("prior-commit", "current-commit")]
    public void IdentitySalvageBelowCapAllowsOrdinaryResolution(
        string? priorCommit, string? currentCommit)
    {
        var fixture = new Fixture();
        fixture.Record(CurrentFindings.Replace("125", "120"), "needs-work", priorCommit);
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, "Review below cap");
        fixture.Record(UnprovenResolutionFindings, "needs-work", currentCommit,
            new ReviewRetryCapReceipt(1, 7));

        var verification = fixture.Reviewer.LastVerification!;
        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode,
            verification.ReviewFindingContractViolation?.Code);
        Assert.NotNull(verification.MergedReviewFindings);
        Assert.Equal(ReviewFindingState.Resolved, verification.MergedReviewFindings
            .Single(finding => finding.StableId == BlockerId).State);
        Assert.Equal("120", verification.MergedReviewFindings
            .Single(finding => finding.StableId == AdvisoryId).Location.Region);
        Assert.Equal(ReviewFindingState.Open, verification.MergedReviewFindings
            .Single(finding => finding.StableId == "new-blocker").State);
    }

    private static void AssertUnprovenResolutionLedger(IReadOnlyList<ReviewFinding>? ledger)
    {
        Assert.NotNull(ledger);
        Assert.Equal(3, ledger.Count);
        var blocker = Assert.Single(ledger, finding => finding.StableId == BlockerId);
        Assert.Equal(ReviewFindingState.Open, blocker.State);
        Assert.Equal(FindingSeverity.Blocking, blocker.Severity);
        Assert.Equal("87-95", blocker.Location.Region);
        var advisory = Assert.Single(ledger, finding => finding.StableId == AdvisoryId);
        Assert.Equal(ReviewFindingState.Open, advisory.State);
        Assert.Equal(FindingSeverity.Advisory, advisory.Severity);
        Assert.Equal("120", advisory.Location.Region);
        var newBlocker = Assert.Single(ledger, finding => finding.StableId == "new-blocker");
        Assert.Equal(ReviewFindingState.Open, newBlocker.State);
        Assert.Equal(FindingSeverity.Blocking, newBlocker.Severity);
    }

    [Xunit.Fact]
    public void B29d517fShapeOnDifferentReviewedCommitMergesBlockerAndKeepsAdvisoryPriorRegion() =>
        AssertShape("prior-commit", "current-commit");

    [Xunit.Fact]
    public void B29d517fShapeOnSameReviewedCommitMatchesCriterionOneLedger() =>
        AssertShape("same-commit", "same-commit");

    [Xunit.Fact]
    public void B29d517fShapeWithEmptyPriorReviewedCommitMatchesCriterionOneLedger() =>
        AssertShape(null, "current-commit");

    [Xunit.Fact]
    public void B29d517fShapeWithEmptyCurrentReviewedCommitMatchesCriterionOneLedger() =>
        AssertShape("prior-commit", null);

    [Xunit.Fact]
    public void EmptyReviewedCommitsKeepOtherFindingsAndPriorAdvisoryAnchor() =>
        AssertShape(null, null);

    [Xunit.Fact]
    public void EmptyCommitsAdvisoryMoveRetainsNewBlockerAndSurvivesReplay()
    {
        var fixture = new Fixture();
        fixture.Record("""
            [{"stable_id":"reviewer-correction-rework-unclassified","state":"open","severity":"advisory","category":"correctness","location":{"file":"ReviewerPassingFindingEvidenceRouter.cs","region":"120"},"description":"Classify correction rework."}]
            """, "pass", null);
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, "Recheck delivery");
        fixture.Record(CurrentFindings, "needs-work", null);
        AssertShapeLedger(fixture.Reviewer);

        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, "Recheck accepted anchors");
        fixture.Record(CurrentFindings.Replace("125", "120"), "needs-work", null);

        AssertShapeLedger(fixture.Reviewer);
        Assert.Null(fixture.Reviewer.LastVerification!.ReviewFindingContractViolation);
        AssertNoFalseNoBlockerFailure(fixture.Goal);
    }

    [Xunit.Fact]
    public void B29d517fShapeDriverRetriesDeveloperWithBlockerAndNoNeedsWorkWithoutOpenFindings()
    {
        var fixture = RecordShape("prior-commit", "current-commit");
        AssertShapeLedger(fixture.Reviewer);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Reviewer.Status);
        Assert.True(WorkerResultBlockers.TryFindNeedsWorkVerdict(fixture.Reviewer.LastVerification, out _));
        Assert.Contains(fixture.Goal.Timeline, evt =>
            evt.TaskId == fixture.Reviewer.Id && evt.Kind == ProgressKind.TaskVerificationRecorded);

        TaskId? retriedTask = null;
        string? retryBrief = null;
        var driver = MakeDriver(retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
        {
            retriedTask = taskId;
            retryBrief = message;
            return fixture.Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
        });

        var result = driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(fixture.Developer.Id, retriedTask);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Developer.Status);
        Assert.Contains(BlockerId, retryBrief, StringComparison.Ordinal);
        Assert.Contains("RESIDUAL_OPEN_ACTION_ITEMS", retryBrief, StringComparison.Ordinal);
        AssertNoFalseNoBlockerFailure(fixture.Goal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("prior-commit", "current-commit")]
    [Xunit.InlineData(null, "current-commit")]
    [Xunit.InlineData("prior-commit", null)]
    public void MovedAdvisoryResubmittedAsBlockingNeverFallsBackToHidingLedger(
        string? priorCommit, string? currentCommit)
    {
        var fixture = new Fixture();
        fixture.Record(PriorFindings.Replace("87-95", "80-90"), "pass", priorCommit);
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, "Recheck delivery");
        fixture.Record(CurrentFindings, "needs-work", currentCommit);

        Assert.Null(fixture.Reviewer.LastVerification!.MergedReviewFindings);
        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode,
            fixture.Reviewer.LastVerification.ReviewFindingContractViolation?.Code);
        var error = Assert.Throws<ReviewFindingConvergenceException>(() => fixture.BuildBrief());
        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode, error.Code);
        Assert.Contains(BlockerId, error.Message, StringComparison.Ordinal);
        var emptyTextError = Assert.Throws<ReviewFindingConvergenceException>(() => fixture.BuildBrief(""));
        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode, emptyTextError.Code);
        Assert.Contains(fixture.Goal.Timeline, evt => evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("structured findings invalid", StringComparison.Ordinal));

        TaskId? retriedTask = null;
        string? retryBrief = null;
        var driver = MakeDriver(retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
        {
            retriedTask = taskId;
            retryBrief = message;
            return fixture.Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
        });
        var result = driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(fixture.Reviewer.Id, retriedTask);
        Assert.Contains(ReviewFindingConvergence.IdentityMovedViolationCode, retryBrief, StringComparison.Ordinal);
        AssertNoFalseNoBlockerFailure(fixture.Goal);
    }

    [Xunit.Fact]
    public void NonIdentityViolationCannotHideSubmittedBlockerBehindPriorLedger()
    {
        var fixture = new Fixture();
        fixture.Record(PriorFindings, "pass", "prior-commit");
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, "Recheck evidence");
        fixture.Record(CurrentFindings.Replace("\"category\":\"correctness\"",
            "\"category\":\"test-evidence\""), "needs-work", "current-commit");

        Assert.Null(fixture.Reviewer.LastVerification!.MergedReviewFindings);
        const string code = "ERR_REVIEWER_TEST_EVIDENCE_REQUEST_MISSING";
        Assert.Equal(code, fixture.Reviewer.LastVerification.ReviewFindingContractViolation?.Code);
        var error = Assert.Throws<ReviewFindingConvergenceException>(() => fixture.BuildBrief());
        Assert.Equal(code, error.Code);
        Assert.Contains(BlockerId, error.Message, StringComparison.Ordinal);
        AssertNoFalseNoBlockerFailure(fixture.Goal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("prior-commit", "current-commit")]
    [Xunit.InlineData(null, "current-commit")]
    [Xunit.InlineData("prior-commit", null)]
    public void MovedOpenBlockingFindingWithoutTouchProofStillRecordsIdentityMoved(
        string? priorCommit, string? currentCommit)
    {
        var fixture = new Fixture();
        fixture.Record(CurrentFindings.Replace("125", "120"), "needs-work", priorCommit);
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, "Recheck blocker");
        fixture.Record(CurrentFindings.Replace("87-95", "100-108").Replace("125", "120"),
            "needs-work", currentCommit);

        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode,
            fixture.Reviewer.LastVerification!.ReviewFindingContractViolation?.Code);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Reviewer.Status);
        if (fixture.Reviewer.LastVerification.MergedReviewFindings is { } ledger)
        {
            Assert.Equal("87-95", ledger.Single(finding => finding.StableId == BlockerId).Location.Region);
        }
        Assert.Contains(fixture.Goal.Timeline, evt =>
            evt.Kind == ProgressKind.ReviewFindingContractViolationRecorded &&
            evt.Message.Contains(BlockerId, StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void NeedsWorkWhoseMergedLedgerHasNoOpenBlockerStillRaisesNeedsWorkWithoutOpenFindings()
    {
        var fixture = new Fixture();
        fixture.Record(PriorFindings, "needs-work", "current-commit");

        Assert.NotNull(fixture.Reviewer.LastVerification!.MergedReviewFindings);
        Assert.Null(fixture.Reviewer.LastVerification.ReviewFindingContractViolation);
        var error = Assert.Throws<ReviewFindingConvergenceException>(() => fixture.BuildBrief());
        Assert.Equal(ReviewFindingConvergence.NeedsWorkWithoutOpenFindingsViolationCode, error.Code);
    }

    [Xunit.Fact]
    public void MovedAdvisoryOnlyViolationIsRecordedOnVerificationWithEvent()
    {
        var fixture = new Fixture();
        fixture.Record(PriorFindings, "pass", "prior-commit");
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, "Recheck advice");
        fixture.Record(PriorFindings.Replace("120", "125"), "pass", null);

        var verification = fixture.Reviewer.LastVerification!;
        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode,
            verification.ReviewFindingContractViolation?.Code);
        Assert.Contains(AdvisoryId, verification.ReviewFindingContractViolation!.Message, StringComparison.Ordinal);
        Assert.NotNull(verification.MergedReviewFindings);
        Assert.Equal("120", verification.MergedReviewFindings.Single(finding =>
            finding.StableId == AdvisoryId).Location.Region);
        Assert.Contains(fixture.Goal.Timeline, evt => evt.TaskId == fixture.Reviewer.Id &&
            evt.Kind == ProgressKind.ReviewFindingContractViolationRecorded &&
            evt.Message.Contains(ReviewFindingConvergence.IdentityMovedViolationCode, StringComparison.Ordinal) &&
            evt.Message.Contains(AdvisoryId, StringComparison.Ordinal));
    }

    private static void AssertShape(string? priorCommit, string? currentCommit)
    {
        var fixture = RecordShape(priorCommit, currentCommit);
        AssertShapeLedger(fixture.Reviewer);
        Assert.Equal(priorCommit, fixture.Reviewer.VerificationHistory[0].ReviewedCommit);
        Assert.Equal(currentCommit, fixture.Reviewer.LastVerification!.ReviewedCommit);
        AssertNoFalseNoBlockerFailure(fixture.Goal);
    }

    private static Fixture RecordShape(string? priorCommit, string? currentCommit)
    {
        var fixture = new Fixture();
        fixture.Record(PriorFindings, "pass", priorCommit);
        Assert.Equal(WorkTaskStatus.Completed, fixture.Reviewer.Status);
        fixture.Kernel.RetryTask(fixture.Goal.Id, fixture.Reviewer.Id, "Recheck delivery");
        fixture.Record(CurrentFindings, "needs-work", currentCommit);
        return fixture;
    }

    private static void AssertShapeLedger(TaskSpec reviewer)
    {
        var ledger = reviewer.LastVerification!.MergedReviewFindings;
        Assert.NotNull(ledger);
        Assert.Equal(2, ledger.Count);
        var blocker = Assert.Single(ledger, finding => finding.StableId == BlockerId);
        Assert.Equal(ReviewFindingState.Open, blocker.State);
        Assert.Equal(FindingSeverity.Blocking, blocker.Severity);
        Assert.Equal("87-95", blocker.Location.Region);
        var advisory = Assert.Single(ledger, finding => finding.StableId == AdvisoryId);
        Assert.Equal(ReviewFindingState.Open, advisory.State);
        Assert.Equal(FindingSeverity.Advisory, advisory.Severity);
        Assert.Equal("120", advisory.Location.Region);
    }

    private static void AssertNoFalseNoBlockerFailure(Goal goal)
    {
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Message.Contains(ReviewFindingConvergence.NeedsWorkWithoutOpenFindingsViolationCode,
                StringComparison.Ordinal) ||
            evt.Message.Contains("zero open blocking structured findings", StringComparison.Ordinal));
    }

    private sealed class Fixture
    {
        private readonly StepClock _clock = new();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal TaskSpec Reviewer { get; }
        internal TaskSpec Developer { get; }

        internal Fixture()
        {
            Kernel = new AgentOrchestratorKernel(_clock);
            Goal = GoalLifecycleCommands.CreateAndActivateGoal(Kernel, DefaultAgents(), "Salvage advisory anchor drift");
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            foreach (var task in Goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                Dispatch(task);
                Kernel.RecordTaskVerification(Goal.Id, task.Id,
                    new TaskVerificationRecord("fixture", @"C:\fixture", 0, "ok", "", _clock.UtcNow));
            }
        }

        internal void Record(string findings, string verdict, string? commit,
            ReviewRetryCapReceipt? reviewRetryCap = null)
        {
            Dispatch(Reviewer, reviewRetryCap);
            if (commit is not null)
                Kernel.RecordDispatchBaseCommit(Goal.Id, Reviewer.Id, commit);
            var stdout = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: none", "commands: review", "tests: pass - fixture",
                "commit: none", "blockers: none", $"findings: {findings}", "touched_anchors: []",
                $"verdict: {verdict}", "model_fit: fixture/model - adequate - review - fixture",
                "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordDispatchExecutionResult(Goal.Id, Reviewer.Id,
                new TaskVerificationRecord("fixture", @"C:\fixture", verdict == "pass" ? 0 : 1,
                    stdout, "", _clock.UtcNow, WorkerResultPresent: true));
        }

        internal string BuildBrief(string currentFinding = "Reattach fresh delivery") =>
            AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            Goal, Developer, Reviewer, currentFinding, "verdict=needs-work", AgentRole.Developer,
            1, "fixture-output", []);

        private void Dispatch(TaskSpec task, ReviewRetryCapReceipt? reviewRetryCap = null) =>
            Kernel.RecordTaskDispatch(Goal.Id, task.Id,
                new TaskDispatchRecord("fixture", "fixture", @"C:\fixture", _clock.UtcNow,
                    ReviewRetryCap: reviewRetryCap));
    }

    private sealed class StepClock : IClock
    {
        private DateTimeOffset _now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => _now = _now.AddSeconds(1);
    }
}
