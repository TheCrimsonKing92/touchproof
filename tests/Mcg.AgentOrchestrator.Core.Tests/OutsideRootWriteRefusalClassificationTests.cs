using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all evidence and dispatch state is in memory; no processes or filesystem I/O.
public sealed class OutsideRootWriteRefusalClassificationTests
{
    private static readonly string Checkout = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "refusal-checkout"));
    private static readonly string Worktree = Path.Combine(Checkout, ".orchestrator-worktrees", "goal");
    private static readonly string ViteOutput = Path.Combine(Checkout, "node_modules", ".vite-temp",
        "vite.config.ts.timestamp-123.mjs");

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void Classify_OnlyViteWriteRefusals_ReportsEnvironmentFault(AgentRole role)
    {
        var task = Task(role);
        var verification = Verification($"fail - npm run check: 87 passed, 13 failed; all failures: {Open(ViteOutput)}") with
        {
            DispatchStartedAt = task.LastDispatch!.DispatchedAt,
            DispatchAttemptAt = task.LastDispatch.DispatchedAt,
            EvidenceSource = VerificationEvidenceSource.Dispatch
        };
        // Reach refusal classification through the subscription Tester's terminal-contract gate.
        if (role == AgentRole.Tester)
            Assert.Equal(VerificationTerminalContractStatus.Valid,
                WorkerResultBlockers.AssessSubscriptionVerification(task, verification).Status);

        var outcome = DispatchFailureClassifier.Classify(task, verification);

        AssertEnvironmentFault(outcome, ViteOutput);
    }

    [Theory]
    [InlineData("AssertionError: expected 2 to equal 1")]
    [InlineData("Assert.Equal() Failure: Expected: 2 Actual: 1")]
    public void Classify_AssertionOnly_RetainsFailingTests(string failure)
    {
        AssertFailingTests(DispatchFailureClassifier.Classify(Task(), Verification("fail - " + failure)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Classify_AssertionAlongsideRefusal_RetainsFailingTests(bool inStderr)
    {
        const string assertion = "AssertionError: expected 2 to equal 1";
        var summary = $"fail - all failures: {Open(ViteOutput)}" + (inStderr ? "" : "; " + assertion);

        var outcome = DispatchFailureClassifier.Classify(Task(),
            Verification(summary, stderr: inStderr ? assertion : ""));

        AssertFailingTests(outcome);
    }

    [Theory]
    [InlineData("node_modules/.vite-temp/vite.config.ts.timestamp-123.mjs")]
    [InlineData(".mcg-sandbox/node/cache")]
    [InlineData(".")]
    public void Classify_WriteWithinPermittedRoots_RetainsFailingTests(string relative)
    {
        var target = Path.GetFullPath(relative, Worktree);

        AssertFailingTests(DispatchFailureClassifier.Classify(Task(),
            Verification($"fail - EACCES: permission denied, mkdir '{target}'")));
    }

    [Fact]
    public void Classify_SiblingWithSamePrefix_IsOutsideWorktree()
    {
        var target = Worktree + "-other" + Path.DirectorySeparatorChar + "cache";

        var outcome = DispatchFailureClassifier.Classify(Task(),
            Verification($"fail - EACCES: permission denied, mkdir '{target}'"));

        AssertEnvironmentFault(outcome, target);
    }

    [Fact]
    public void Classify_RelativeWrite_UsesVerificationWorkingDirectory()
    {
        var verificationDirectory = Path.Combine(Worktree, "src");
        var relative = Path.Combine("..", "..", "..", "node_modules", ".vite-temp",
            "vite.config.ts.timestamp-123.mjs");
        Assert.Equal(ViteOutput, Path.GetFullPath(relative, verificationDirectory));

        var outcome = DispatchFailureClassifier.Classify(Task(),
            Verification($"fail - {Open(relative)}", workingDirectory: verificationDirectory));

        AssertEnvironmentFault(outcome, ViteOutput);
    }

    [Theory]
    [InlineData("EPERM: operation not permitted, open '{path}' flags='w'")]
    [InlineData("EACCES: permission denied, mkdir '{path}'")]
    [InlineData("EACCES: access denied, write '{path}'")]
    [InlineData("writing: Access to the path '{path}' is denied.")]
    public void Classify_ExplicitExternalWrite_IsStackIndependent(string diagnostic)
    {
        var target = Path.Combine(Checkout, "tool-cache", "output.bin");

        var outcome = DispatchFailureClassifier.Classify(Task(lowIntegrity: false),
            Verification("fail - " + diagnostic.Replace("{path}", target)));

        AssertEnvironmentFault(outcome, target);
    }

    [Theory]
    [InlineData("EPERM: operation not permitted, open '{path}'")]
    [InlineData("EPERM: operation not permitted, open '{path}' flags='r'")]
    [InlineData("EACCES: permission denied, stat '{path}'")]
    [InlineData("Access to the path '{path}' is denied.")]
    [InlineData("EPERM: operation not permitted")]
    public void Classify_InsufficientWriteEvidence_RetainsFailingTests(string diagnostic)
    {
        var target = Path.Combine(Checkout, "input.txt");

        AssertFailingTests(DispatchFailureClassifier.Classify(Task(),
            Verification("fail - " + diagnostic.Replace("{path}", target))));
    }

    [Fact]
    public void Classify_ViteOpenWithoutLowSandbox_RetainsFailingTests()
    {
        AssertFailingTests(DispatchFailureClassifier.Classify(Task(lowIntegrity: false),
            Verification("fail - " + Open(ViteOutput))));
    }

    [Fact]
    public void Classify_ExternalAndLocalRefusals_RetainsFailingTests()
    {
        var local = Path.Combine(Worktree, "local-cache");
        AssertFailingTests(DispatchFailureClassifier.Classify(Task(),
            Verification($"fail - all failures: {Open(ViteOutput)}; EACCES: permission denied, mkdir '{local}'")));
    }

    [Fact]
    public void Classify_UnexplainedAdditionalFailure_RetainsFailingTests()
    {
        AssertFailingTests(DispatchFailureClassifier.Classify(Task(),
            Verification($"fail - {Open(ViteOutput)}; another test failed for an unknown reason")));
    }

    [Fact]
    public void Classify_PreparationFailureBeforeWorkerResult_ReportsEnvironmentFault()
    {
        var stderr = "[dispatch-host] worker launch/run failed: " +
            "Mcg.AgentOrchestrator.Infrastructure.WorkerSandboxRefusedException: " +
            $"WORKER_SANDBOX_REFUSED reason=node-dependency-preparation-failed path={Worktree}" +
            " ---> System.InvalidOperationException: Node install exit=1; install diagnostics";
        var verification = new TaskVerificationRecord("codex exec", Worktree, 1, "", stderr,
            new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));

        var outcome = DispatchFailureClassifier.Classify(Task(), verification);

        Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
        Assert.Equal("worker-sandbox-refused", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Contains("reason=node-dependency-preparation-failed", outcome.EvidenceSummary);
    }

    private static string Open(string path) => $"EPERM: operation not permitted, open '{path}'";

    private static TaskSpec Task(AgentRole role = AgentRole.Developer, bool lowIntegrity = true)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Classify external writes", [new TaskSpec(TaskId.New(), "Verify change", role)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = Assert.Single(goal.Tasks);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", Worktree,
            clock.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli, SandboxLowIntegrity: lowIntegrity));
        return task;
    }

    private static TaskVerificationRecord Verification(string tests, string stderr = "", string? workingDirectory = null)
    {
        var stdout = $"WORKER_RESULT:\nfiles: none\ncommands: npm run check\ntests: {tests}\ncommit: none\nblockers: none\nmodel_fit: fixture\nskills: none\nconfidence: high\nEND_WORKER_RESULT";
        return new TaskVerificationRecord("codex exec", workingDirectory ?? Worktree, 0, stdout, stderr,
            new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), WorkerResultPresent: true,
            HeartbeatStandardOutputBytes: stdout.Length);
    }

    private static void AssertEnvironmentFault(DispatchOutcome outcome, string target)
    {
        Assert.Equal(TaskOutcomeClass.Environmental, outcome.OutcomeClass);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Equal("worker-sandbox-refused", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal($"worker sandbox refused: reason=write-outside-worktree path={target}", outcome.EvidenceSummary);
    }

    private static void AssertFailingTests(DispatchOutcome outcome)
    {
        Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Equal("succeeded-worker-result-failing-tests", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }
}
