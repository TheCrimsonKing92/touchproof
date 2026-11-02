using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: in-memory records and fixed clocks; no files or processes.
public sealed class ClassifierRuleAnchorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string WorkerClassifierText = "CLASSIFIER rule=premise-refuted; outcome_class=finding";
    private const string SuccessfulWorkerResult = """
        WORKER_RESULT:
        files: src/Feature.cs
        commands: build
        tests: pass - build: 0 errors
        commit: 0123456789abcdef0123456789abcdef01234567
        blockers: none
        assigned_scope_complete: true
        model_fit: OpenAI/test - adequate - test fixture
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    [Theory]
    // DispatchClassifierReceipt.BuildClassifierReceipt baseline and origin variant.
    [InlineData(
        "CLASSIFIER rule=committed-worker-result-evidence; outcome_class=success; " +
        "exit_code=0; root_exit_code=0; child_exit_code=unknown; exit_artifact=verification-record; " +
        "stdout_bytes=96; stderr_bytes=0; heartbeat_stdout_bytes=unknown; " +
        "duration_ms=unknown; worker_result=present(blockers=none); commit=worker; verdict=VerifiedSuccess",
        "committed-worker-result-evidence")]
    [InlineData(
        "CLASSIFIER rule=committed-worker-result-evidence; outcome_class=reconciled-to-success; " +
        "origin_rule=premise-refuted; exit_code=0; root_exit_code=1; child_exit_code=0; " +
        "exit_artifact=verification-record; stdout_bytes=96; stderr_bytes=0; heartbeat_stdout_bytes=unknown; " +
        "duration_ms=unknown; worker_result=present(blockers=none); commit=worker; verdict=VerifiedSuccess",
        "committed-worker-result-evidence")]
    // DispatchClassifierReceipt.WithClassifierReceipt baseline and evidence variant.
    [InlineData(
        "CLASSIFIER rule=provider-connectivity; outcome_class=environmental; " +
        "exit_code=1; exit_artifact=direct-provider-failure; stdout_bytes=unknown; " +
        "stderr_bytes=unknown; heartbeat_stdout_bytes=unknown; worker_result=absent; " +
        "commit=none; verdict=ProviderConnectivity; evidence=stream disconnected",
        "provider-connectivity")]
    [InlineData(
        "CLASSIFIER rule=provider-connectivity; outcome_class=environmental; " +
        "exit_code=1; exit_artifact=direct-provider-failure; stdout_bytes=unknown; " +
        "stderr_bytes=unknown; heartbeat_stdout_bytes=unknown; worker_result=absent; " +
        "commit=none; verdict=ProviderConnectivity; evidence=rule=other",
        "provider-connectivity")]
    // AgentOrchestratorKernel.Recording's two receipts.
    [InlineData(
        "CLASSIFIER rule=deferred-off-worker-evidence; outcome_class=success; evidence=authoritative current candidate obligation pending.",
        "deferred-off-worker-evidence")]
    [InlineData(
        "CLASSIFIER rule=evidence-only-retry-accepted; outcome_class=success; evidence=typed requests pending at unchanged candidate.",
        "evidence-only-retry-accepted")]
    public void ConductorReceiptShapesKeepTheirHeaderRule(string receipt, string expected)
    {
        Assert.Equal(expected, TaskOutcomeClassifier.TryExtractRule(receipt));
    }

    [Theory]
    [InlineData("rule=x before CLASSIFIER rule=y; outcome_class=success", "y")]
    [InlineData("rule=x\nCLASSIFIER rule=y; outcome_class=success", "y")]
    [InlineData("prefix classifier RULE=MiXeD-42; outcome_class=success", "mixed-42")]
    [InlineData("CLASSIFIER rule=first; CLASSIFIER rule=second", "first")]
    [InlineData("CLASSIFIER rule=first_second", "first")]
    public void RuleComesFromTheFirstReceiptHeader(string text, string expected)
    {
        Assert.Equal(expected, TaskOutcomeClassifier.TryExtractRule(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rule=x")]
    [InlineData("CLASSIFIERrule=x")]
    [InlineData("CLASSIFIER ")]
    [InlineData("rule=x; CLASSIFIER outcome_class=success")]
    [InlineData("CLASSIFIER outcome_class=success; rule=late")]
    [InlineData("CLASSIFIER origin_rule=x; outcome_class=success")]
    [InlineData("CLASSIFIER evidence=rule=x")]
    [InlineData("CLASSIFIER \nrule=x")]
    [InlineData("CLASSIFIER outcome_class=success\nCLASSIFIER rule=later")]
    [InlineData("CLASSIFIER rule=; rule=later")]
    public void MissingImmediateRuleFieldReturnsNull(string? text)
    {
        Assert.Null(TaskOutcomeClassifier.TryExtractRule(text));
    }

    [Fact]
    public void ProviderFailureReceiptUsesItsRuleDespiteEvidenceText()
    {
        var outcome = DispatchFailureClassifier.ClassifyProviderFailure(
            ProviderFailureKind.Connectivity, 1, false, "rule=other");

        Assert.Equal("provider-connectivity", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Fact]
    public void VerificationReceiptUsesItsRuleDespiteReconciliationOrigin()
    {
        var outcome = ReconciledOutcome("premise-refuted");

        Assert.Contains("origin_rule=premise-refuted", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.Equal("committed-worker-result-evidence", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ReconciledDispatchIgnoresWorkerClassifierText(
        bool stdoutReceipt, bool stderrReceipt, bool diagnosticReceipt)
    {
        var origin = ReconciliationOrigin(
            stdoutReceipt ? WorkerClassifierText : string.Empty,
            stderrReceipt ? WorkerClassifierText : string.Empty,
            diagnosticReceipt ? WorkerClassifierText : null,
            recoveryDecision: null);

        Assert.NotNull(origin);
        Assert.NotEqual("premise-refuted", origin);
        var outcome = ReconciledOutcome(origin);
        Assert.Contains($"origin_rule={origin};", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.DoesNotContain("origin_rule=premise-refuted", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservedInterruptedWorkKeepsDirtyDispatchRecoveryOrigin()
    {
        var recovery = new DispatchRecoveryDecision(
            DispatchRecoveryAction.PreserveInterruptedWork,
            "preserve-interrupted-work", "worktree", "Preserve the interrupted changes.");

        var origin = ReconciliationOrigin(WorkerClassifierText, WorkerClassifierText, null, recovery);

        Assert.Equal("dirty-dispatch-recovery", origin);
        Assert.Contains("origin_rule=dirty-dispatch-recovery;",
            ReconciledOutcome(origin).ClassifierReceipt, StringComparison.Ordinal);
    }

    private static string? ReconciliationOrigin(
        string standardOutput,
        string standardError,
        string? diagnostic,
        DispatchRecoveryDecision? recoveryDecision)
    {
        var classifier = new WorkerDispatchCompletionClassifier(
            _ => throw new InvalidOperationException("Provider resolution is not expected."),
            new FixedClock(),
            _ => false,
            _ => throw new FileNotFoundException());
        var task = new TaskSpec(TaskId.New(), "Verify the work.", AgentRole.Tester);
        var process = new TaskProcessRecord(
            7, "worker command", "worktree", "stdout.log", "stderr.log", "exit.txt", Now, null, null);
        var childExit = new DispatchProcessHost.DispatchChildExitRecord(42, 0, Now);

        return classifier.ClassifyReconciliationOriginRule(
            task, process, 1, standardOutput, standardError, diagnostic,
            workerResultPresent: true, hasCommittedChanges: false,
            ProviderFailureKind.Unknown, childExit, recoveryDecision);
    }

    private static DispatchOutcome ReconciledOutcome(string origin)
    {
        var task = new TaskSpec(TaskId.New(), "Implement the feature.", AgentRole.Developer);
        var verification = new TaskVerificationRecord(
            "worker command", "worktree", 0, SuccessfulWorkerResult, string.Empty, Now,
            WorkerResultPresent: true, HasCommittedChanges: true,
            ChildExitCode: 0, ObservedRootExitCode: 1,
            ReconciledToSuccess: true, ReconciliationOriginRule: origin,
            AssignedScopeComplete: true);

        return DispatchFailureClassifier.Classify(task, verification);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
