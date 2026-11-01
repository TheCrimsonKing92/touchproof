using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: in-memory task evidence and a fixed clock; no processes or shared state.
public sealed class DispatchClassifierPartialConversionTests
{
    private static readonly DateTimeOffset CompletedAt =
        new(2026, 10, 5, 10, 12, 56, TimeSpan.Zero);
    private static readonly string Candidate = new('b', 40);

    [Fact]
    public void AbsoluteReset_ChicagoNoon_PreservesUtcReset()
    {
        // Pre-move b3a224571: SubscriptionLimitEvidenceTests.AbsoluteReset_ChicagoNoon_ReturnsUtcReset.
        Assert.True(CliRefusalSignals.TryGetAbsoluteResetRetryAfter(
            "You've hit your weekly limit \u00B7 resets Oct 7, 12pm (America/Chicago)",
            CompletedAt, out var retryAfter));

        Assert.Equal(new DateTimeOffset(2026, 10, 7, 17, 0, 0, TimeSpan.Zero), retryAfter);
    }

    [Fact]
    public void AbsoluteReset_ImpossibleDate_ReturnsFalseAndDefault()
    {
        // The pre-move parser initializes the out value before rejecting an impossible date.
        Assert.False(CliRefusalSignals.TryGetAbsoluteResetRetryAfter(
            "You've hit your weekly limit \u00B7 resets Feb 30, 12pm (America/Chicago)",
            CompletedAt, out var retryAfter));

        Assert.Equal(default(DateTimeOffset), retryAfter);
    }

    [Theory]
    [InlineData("API Error: 529", true)]
    [InlineData("API Error: 503 Overloaded", true)]
    [InlineData("API Error: 429 Overloaded", false)]
    [InlineData("The fixture mentions API Error 529 Overloaded.", false)]
    public void ApiOverload_StatusAndPrefix_PreserveRecognition(string line, bool expected)
    {
        // Pre-move b3a224571: CliProviderRefusalClassificationTests pins these classifications.
        Assert.Equal(expected, CliRefusalSignals.IsApiErrorOverloadLine(line));
    }

    [Theory]
    [InlineData("AlphaTests", true, false, true)]
    [InlineData("BetaTests", true, false, false)]
    [InlineData("AlphaTests", false, false, false)]
    [InlineData("AlphaTests", true, true, false)]
    public void DeferredRound_EvidenceAndCommitState_PreserveRecognition(
        string outputClass, bool workerResultPresent, bool hasCommittedChanges, bool expected)
    {
        // Pre-move b3a224571: DeveloperDeferredNoChangeClassificationTests uses this marker/fixture.
        var clock = new FakeClock();
        clock.Advance(CompletedAt.AddMinutes(-1) - clock.UtcNow);
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Retry candidate", AgentRole.Developer);
        var goal = kernel.CreateGoal("Deferred no-change classification", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RetryTask(goal.Id, task.Id, "Review candidate again.");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", @"C:\repo", clock.UtcNow, BaseCommit: Candidate));
        var marker = new DeferredNoChangeOutcome(Candidate, ["AlphaTests"],
            "NO_CHANGE: the candidate has the fix.").FormatMarker();
        var output = "NO_CHANGE: the candidate has the fix.\nWORKER_RESULT:\nfiles: none\n" +
            "commands: none\ntests: deferred - " + outputClass +
            "\ncommit: none\nblockers: none\nassigned_scope_complete: true\n" +
            "model_fit: test/model - adequate - fixture\nskills: none\n" +
            "confidence: high\nEND_WORKER_RESULT";
        var verification = new TaskVerificationRecord(
            "codex exec", @"C:\repo", 0, output, marker, CompletedAt);

        Assert.Equal(expected, DeveloperDeferredNoChangeRound.IsDeveloperDeferredNoChangeRound(
            task, verification, workerResultPresent, hasCommittedChanges));
    }

    [Fact]
    public void SandboxRefusal_WireLine_PreservesOutcomeAndSummary()
    {
        // Pre-move b3a224571: WorkerSandboxRefusedClassificationTests pins this wire line/outcome.
        var task = new TaskSpec(TaskId.New(), "Classify sandbox refusal", AgentRole.Developer);
        var verification = new TaskVerificationRecord("codex exec", @"C:\repo", 1, "",
            @"WORKER_SANDBOX_REFUSED reason=shared-git-metadata-low-writable path=C:\repo-shared\objects",
            CompletedAt);

        Assert.True(WorkerSandboxRefusal.TryClassifyWorkerSandboxRefusal(
            task, verification, workerResultPresent: false, hasCommittedChanges: false, out var outcome));

        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Equal("worker-sandbox-refused", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(@"worker sandbox refused: reason=shared-git-metadata-low-writable path=C:\repo-shared\objects",
            outcome.EvidenceSummary);
    }

    [Theory]
    [InlineData("log: saw WORKER_SANDBOX_REFUSED reason=x path=y", 1, false)]
    [InlineData("WORKER_SANDBOX_REFUSED reason=x path=y", 0, false)]
    [InlineData("WORKER_SANDBOX_REFUSED reason=x path=y", 1, true)]
    public void SandboxRefusal_ProseOrCompletedWorker_DoesNotClassify(
        string line, int exitCode, bool workerResultPresent)
    {
        var task = new TaskSpec(TaskId.New(), "Reject quoted refusal", AgentRole.Developer);
        var verification = new TaskVerificationRecord(
            "codex exec", @"C:\repo", exitCode, "", line, CompletedAt);

        Assert.False(WorkerSandboxRefusal.TryClassifyWorkerSandboxRefusal(
            task, verification, workerResultPresent, hasCommittedChanges: false, out var outcome));
        Assert.Null(outcome);
    }

    [Fact]
    public void DetachedRounds_MatchingSuffix_PreservesCountAndStopBoundary()
    {
        // Pre-move b3a224571: DetachedRoundWithoutWorkerResultRecordingTests pins the suffix count.
        var task = new TaskSpec(TaskId.New(), "Count detached rounds", AgentRole.Reviewer);
        Assert.Equal(0, DetachedWithoutWorkerResultRounds.CountConsecutiveDetachedWithoutWorkerResultRounds(task));
        foreach (var rule in new[] { "detached-without-worker-result", "provider-interruption",
                     "detached-without-worker-result", "detached-without-worker-result" })
        {
            task.RecordVerification(new TaskVerificationRecord(
                "round", @"C:\repo", 0, "The source has been inspected.", "", CompletedAt));
            task.RecordCompletionVerdict(false, rule);
        }

        Assert.Equal(2, DetachedWithoutWorkerResultRounds.CountConsecutiveDetachedWithoutWorkerResultRounds(task));
    }
}
