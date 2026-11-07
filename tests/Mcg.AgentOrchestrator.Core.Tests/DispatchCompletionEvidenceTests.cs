using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: fixed in-memory records; no files, shared state or processes.
public sealed class DispatchCompletionEvidenceTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly string BaseCommit = new('a', 40);
    private static readonly string ResultCommit = new('c', 40);

    [Theory]
    [InlineData("Test run successful")]
    [InlineData("Tests: Passed: 4; Failed: 0")]
    public void Completion_PassingVerificationAndCommit_ReturnsTrue(string output)
    {
        var task = DeveloperTask();
        task.RecordDispatch(Dispatch(BaseCommit, ResultCommit));
        var verification = Verification(output);

        // Baseline a1da0b5b1: DispatchFailureClassifier.cs:1330-1399.
        Assert.True(DispatchCompletionEvidence.HasDispatchResultCommitEvidence(task));
        Assert.True(DispatchCompletionEvidence.HasVerificationEvidence(output, string.Empty));
        Assert.True(DispatchCompletionEvidence.HasVerificationEvidence(verification));
        Assert.True(DispatchCompletionEvidence.HasDispatchCompletionEvidence(
            task, verification, workerResultPresent: true, hasCommittedChanges: true));
        Assert.True(DispatchCompletionEvidence.HasDispatchCompletionEvidence(
            task, verification, workerResultPresent: true, hasCommittedChanges: false));

        task.RecordDispatch(Dispatch(BaseCommit, BaseCommit));
        // Baseline a1da0b5b1: DispatchFailureClassifier.cs:1330-1339.
        Assert.False(DispatchCompletionEvidence.HasDispatchResultCommitEvidence(task));
    }

    [Fact]
    public void DeveloperNoChange_IntegratedRetry_ReturnsTrue()
    {
        var task = DeveloperTask();
        var goalId = GoalId.New();
        var retryAt = CompletedAt.AddMinutes(-2);
        task.RecordRetry(retryAt, RetryCause.MainDriftConflict);
        var receipt = new PreDispatchIntegrationReceipt(
            goalId, task.Id, retryAt, BaseCommit, new string('b', 40), ResultCommit);
        task.RecordDispatch(Dispatch(ResultCommit, ResultCommit) with
        {
            GoalId = goalId,
            PreDispatchIntegrationReceipt = receipt
        });
        var verification = Verification(WorkerOutput("pass - Passed: 4; Failed: 0"));

        Assert.True(receipt.Matches(goalId, task, task.LastDispatch!.BaseCommit));
        // Baseline a1da0b5b1: DispatchFailureClassifier.cs:1446-1482.
        Assert.False(DispatchCompletionEvidence.HasDispatchResultCommitEvidence(task));
        Assert.True(DispatchCompletionEvidence.IsDeveloperVerifiedNoChangeRound(
            task, verification, workerResultPresent: true, hasCommittedChanges: false));
        Assert.False(DispatchCompletionEvidence.IsDeveloperVerifiedNoChangeRound(
            task, verification, workerResultPresent: true, hasCommittedChanges: true));

        task.RecordDispatch(Dispatch(ResultCommit, ResultCommit));
        // Baseline a1da0b5b1: DispatchFailureClassifier.cs:1453-1477 requires a receipt.
        Assert.False(DispatchCompletionEvidence.IsDeveloperVerifiedNoChangeRound(
            task, verification, workerResultPresent: true, hasCommittedChanges: false));
    }

    [Fact]
    public void DeveloperNoChange_FirstDispatch_ReturnsFalse()
    {
        var task = DeveloperTask();
        task.RecordDispatch(Dispatch(BaseCommit, BaseCommit));
        var verification = Verification(WorkerOutput("pass - Passed: 4; Failed: 0"));

        // Baseline a1da0b5b1: DispatchFailureClassifier.cs:1459-1477,1522-1525.
        Assert.False(DispatchCompletionEvidence.IsDeveloperVerifiedNoChangeRound(
            task, verification, workerResultPresent: true, hasCommittedChanges: false));
    }

    [Fact]
    public void RecognisedNoChange_PassingWorkerResult_ReturnsTrue()
    {
        var failure = new DispatchFailureClassifier.OrchestratorAuthoredFailure(
            TaskOutcomeRules.RequiredFileChangeEvidenceMissing, "No new change evidence.");
        var verification = Verification(WorkerOutput("pass - Passed: 4; Failed: 0")) with
        {
            ExitCode = 1,
            StandardError = DispatchRejectionDiagnosticMarker.Format(true, 0, "none")
        };

        // Baseline a1da0b5b1: DispatchFailureClassifier.cs:1502-1518.
        Assert.True(DispatchCompletionEvidence.IsVerifiedNoChangeRoundWithRecognisedEvidence(
            failure, verification, workerResultPresent: true, hasCommittedChanges: false));
        Assert.False(DispatchCompletionEvidence.IsVerifiedNoChangeRoundWithRecognisedEvidence(
            failure, verification, workerResultPresent: true, hasCommittedChanges: true));
    }

    [Theory]
    [InlineData("not-run - no execution yet", false)]
    [InlineData("deferred - DispatchCompletionEvidenceTests", true)]
    public void RetryRound_NoCommit_UsesWorkerResultDeferral(string tests, bool deferred)
    {
        var task = DeveloperTask();
        task.IncrementCriterionRetryCount();
        var output = WorkerOutput(tests);
        var verification = Verification(output);

        // Baseline a1da0b5b1: DispatchFailureClassifier.cs:1422-1432,1578-1588.
        Assert.Equal(deferred, DispatchCompletionEvidence.HasWorkerResultDeferral(output.Split('\n')));
        Assert.Equal(deferred, DispatchFailureClassifier.HasWorkerResultDeferralInOutput(output));
        Assert.Equal(!deferred, DispatchCompletionEvidence.IsRetryRoundWithoutCommitOrDeferral(
            task, verification, workerResultPresent: true, hasCommittedChanges: false));
        // Baseline a1da0b5b1: DispatchFailureClassifier.cs:1372-1396.
        Assert.Equal(deferred, DispatchCompletionEvidence.HasDispatchCompletionEvidence(
            task, verification, workerResultPresent: true, hasCommittedChanges: false));
    }

    [Theory]
    [InlineData("pass - Passed: 4; Failed: 0", true)]
    [InlineData("fail - Passed: 3; Failed: 1", false)]
    public void StructuredTests_PassingText_RespectsStructuredStatus(string tests, bool green)
    {
        var task = DeveloperTask();
        var verification = Verification("Test run successful\n" + WorkerOutput(tests));

        // Baseline a1da0b5b1: DispatchFailureClassifier.cs:1342-1367.
        Assert.True(DispatchCompletionEvidence.HasVerificationEvidence(verification));
        // Baseline a1da0b5b1: DispatchFailureClassifier.cs:1372-1412,1591-1612.
        Assert.Equal(green, DispatchCompletionEvidence.HasGreenCommittedWorkerResultEvidence(
            verification, workerResultPresent: true, hasCommittedChanges: true));
        Assert.Equal(green, DispatchCompletionEvidence.HasDispatchCompletionEvidence(
            task, verification, workerResultPresent: true, hasCommittedChanges: false));
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("failed", false)]
    [InlineData("unavailable", false)]
    [InlineData("passed", true)]
    [InlineData("mismatch", true)]
    [InlineData("semantic", true)]
    [InlineData("legacy", true)]
    public void BoundCorrection_NoChangeRequiresCurrentPassingReceipt(string scenario, bool accepted)
    {
        var task = DeveloperTask();
        var goalId = GoalId.New();
        var retryAt = CompletedAt.AddMinutes(-2);
        task.RecordRetry(retryAt, RetryCause.MainDriftConflict);
        task.RecordCriterionRetryFeedback(["alpha.cs exceeds the ceiling"]);
        var obligation = scenario switch
        {
            "legacy" => null,
            "mismatch" => RetryCorrectionObligation.ForSourceSizeRatchet(BaseCommit),
            "semantic" => new RetryCorrectionObligation(RetryCorrectionObligationKind.Semantic, ResultCommit, null, ""),
            _ => RetryCorrectionObligation.ForSourceSizeRatchet(ResultCommit)
        };
        task.RecordAcceptedRetryFeedback("alpha.cs exceeds the ceiling", retryAt, obligation: obligation);
        task.RecordDispatch(Dispatch(ResultCommit, ResultCommit) with
        {
            GoalId = goalId,
            PreDispatchIntegrationReceipt = new PreDispatchIntegrationReceipt(
                goalId, task.Id, retryAt, BaseCommit, new string('b', 40), ResultCommit)
        });
        Assert.True(task.LastDispatch!.PreDispatchIntegrationReceipt!.Matches(goalId, task, ResultCommit));
        var error = scenario is "passed" or "failed" or "unavailable"
            ? RetryCorrectionObligationEvidence.Format(task.AcceptedRetryFeedback!.Obligation!, scenario switch
            {
                "passed" => RetryCorrectionConditionVerdict.Passed,
                "failed" => RetryCorrectionConditionVerdict.Failed,
                _ => RetryCorrectionConditionVerdict.Unavailable
            }) : string.Empty;
        var verification = Verification("NO_CHANGE: old finding repaired\n" + WorkerOutput("pass - Passed: 4; Failed: 0"))
            with { StandardError = error };

        Assert.Equal(accepted, DispatchCompletionEvidence.IsDeveloperVerifiedNoChangeRound(task, verification, true, false));
        Assert.Equal(accepted, DispatchCompletionEvidence.HasDispatchCompletionEvidence(task, verification, true, false));
        if (scenario is "missing" or "failed" or "unavailable")
            Assert.NotEqual(DispatchOutcomeKind.VerifiedSuccess,
                DispatchFailureClassifier.Classify(task, verification, workerResultPresent: true).Kind);
    }

    [Fact]
    public void NewestObligationReceipt_FailureOverridesEarlierPass()
    {
        var task = DeveloperTask();
        task.RecordDispatch(Dispatch(ResultCommit, ResultCommit));
        task.RecordAcceptedRetryFeedback("current", CompletedAt,
            obligation: RetryCorrectionObligation.ForSourceSizeRatchet(ResultCommit));
        var obligation = task.AcceptedRetryFeedback!.Obligation!;
        var pass = RetryCorrectionObligationEvidence.Format(obligation, RetryCorrectionConditionVerdict.Passed);
        var fail = RetryCorrectionObligationEvidence.Format(obligation, RetryCorrectionConditionVerdict.Failed);
        Assert.False(RetryCorrectionObligationEvidence.IsSatisfied(task, pass + "\n" + fail));
        Assert.True(RetryCorrectionObligationEvidence.IsSatisfied(task, fail + "\n" + pass));
    }

    private static TaskSpec DeveloperTask() =>
        new(TaskId.New(), "Verify completion evidence.", AgentRole.Developer);

    private static TaskDispatchRecord Dispatch(string baseCommit, string resultCommit) =>
        new("codex-cli", "opaque command", "C:\\repo", CompletedAt.AddMinutes(-1),
            BaseCommit: baseCommit, ResultCommit: resultCommit);

    private static TaskVerificationRecord Verification(string output) =>
        new("opaque command", "C:\\repo", 0, output, string.Empty, CompletedAt);

    private static string WorkerOutput(string tests) => $"""
        WORKER_RESULT:
        files: none
        commands: none
        tests: {tests}
        commit: none
        blockers: none
        END_WORKER_RESULT
        """;
}
