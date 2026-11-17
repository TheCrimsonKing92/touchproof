using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: in-memory kernels and fixture-owned temporary files; no provider or process is launched.
public sealed class GrokBudgetExhaustionTerminalContractTests : WorkerDispatchTestSupport
{
    // Reconstructed from the observed wording in ProviderBudgetExhaustionClassifierTests;
    // original incident bytes are unavailable, so this is not a verbatim log capture.
    private const string GrokBudgetDiagnostic =
        "API error (status 402 Payment Required): Grok Build usage balance exhausted";

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void CompletedRound_Grok402WithoutResult_StoresBudgetFailure(AgentRole role)
    {
        using var fixture = new Fixture(role);

        var verification = fixture.CompleteRound(string.Empty, GrokBudgetDiagnostic);

        Assert.Same(verification, Assert.Single(fixture.Task.VerificationHistory));
        Assert.Same(verification, fixture.Task.LastVerification);
        Assert.Equal(1, verification.ExitCode);
        Assert.False(verification.WorkerResultPresent);
        Assert.Equal(string.Empty, verification.StandardOutput);
        Assert.Equal(GrokBudgetDiagnostic, verification.FullStandardError);
        Assert.StartsWith(GrokBudgetDiagnostic, verification.StandardError, StringComparison.Ordinal);
        Assert.Equal(ProviderFailureKind.BudgetExhausted, verification.ProviderFailureKind);
        Assert.Equal(VerificationTerminalContractStatus.Missing,
            WorkerResultBlockers.AssessSubscriptionVerification(fixture.Task, verification).Status);

        var outcome = DispatchFailureClassifier.Classify(fixture.Task, verification);
        Assert.Equal(DispatchOutcomeKind.ProviderBudgetExhausted, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Contains("rule=provider-budget-exhausted", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.DoesNotContain("verification-terminal-contract-incomplete", outcome.ClassifierReceipt,
            StringComparison.Ordinal);
        Assert.Equal("provider-budget-exhausted", verification.CompletionVerdictRule);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.Empty(RepairEntries(fixture));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, VerificationTerminalContractStatus.Missing)]
    [InlineData(AgentRole.Tester, VerificationTerminalContractStatus.Missing)]
    [InlineData(AgentRole.Reviewer, VerificationTerminalContractStatus.Malformed)]
    [InlineData(AgentRole.Tester, VerificationTerminalContractStatus.Malformed)]
    public void RecordedRound_InvalidContract_ExhaustsTwoRepairs(
        AgentRole role, VerificationTerminalContractStatus contractStatus)
    {
        using var fixture = new Fixture(role);
        var output = contractStatus == VerificationTerminalContractStatus.Missing
            ? "Introduction only."
            : "WORKER_RESULT:\ntests: pass - fixture evidence\nblockers: none";

        for (var round = 1; round <= 3; round++)
        {
            var timelineCount = fixture.Goal.Timeline.Count;
            fixture.RecordRound(output);
            var verification = fixture.Task.VerificationHistory[^1];
            Assert.Equal(round, fixture.Task.VerificationHistory.Count);
            Assert.Equal(ProviderFailureKind.Unknown, verification.ProviderFailureKind);
            Assert.Equal(string.Empty, verification.StandardError);
            Assert.Equal(contractStatus,
                WorkerResultBlockers.AssessSubscriptionVerification(fixture.Task, verification).Status);
            var outcome = DispatchFailureClassifier.Classify(fixture.Task, verification);
            Assert.Equal(DispatchOutcomeKind.VerificationIncomplete, outcome.Kind);
            Assert.Contains("rule=verification-terminal-contract-incomplete", outcome.ClassifierReceipt,
                StringComparison.Ordinal);

            var events = fixture.Goal.Timeline.Skip(timelineCount)
                .Where(entry => entry.TaskId == fixture.Task.Id).ToArray();
            if (round <= 2)
            {
                Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
                var retry = Assert.Single(events, entry => entry.Kind == ProgressKind.TaskRetried);
                Assert.StartsWith($"{role} terminal contract repair {round}/2; same-role retry:", retry.Message);
                Assert.DoesNotContain(events, entry => entry.Kind == ProgressKind.TaskFailed);
            }
            else
            {
                Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
                Assert.DoesNotContain(events, entry => entry.Kind == ProgressKind.TaskRetried);
                var failure = Assert.Single(events, entry => entry.Kind == ProgressKind.TaskFailed);
                Assert.StartsWith(
                    $"{role} terminal contract repair budget exhausted; operator action required:", failure.Message);
            }
            Assert.Equal(Math.Min(round, 2), RepairEntries(fixture).Count());
        }
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, "API error (status 401 Unauthorized): Please sign in again.",
        ProviderFailureKind.AuthenticationRequired, DispatchOutcomeKind.ProviderAuthentication,
        RecoveryRecommendation.OperatorNeeded, "provider-authentication")]
    [InlineData(AgentRole.Tester, "API error (status 401 Unauthorized): Please sign in again.",
        ProviderFailureKind.AuthenticationRequired, DispatchOutcomeKind.ProviderAuthentication,
        RecoveryRecommendation.OperatorNeeded, "provider-authentication")]
    [InlineData(AgentRole.Reviewer, "ERROR: HTTP 429 Too Many Requests",
        ProviderFailureKind.RateLimit, DispatchOutcomeKind.RecoverableSubscriptionLimit,
        RecoveryRecommendation.AutoRetry, "subscription-limit")]
    [InlineData(AgentRole.Tester, "ERROR: HTTP 429 Too Many Requests",
        ProviderFailureKind.RateLimit, DispatchOutcomeKind.RecoverableSubscriptionLimit,
        RecoveryRecommendation.AutoRetry, "subscription-limit")]
    public void CompletedRound_OtherProviderFault_PreservesRouting(
        AgentRole role, string error, ProviderFailureKind failureKind,
        DispatchOutcomeKind outcomeKind, RecoveryRecommendation recovery, string rule)
    {
        using var fixture = new Fixture(role);

        var verification = fixture.CompleteRound(string.Empty, error);

        Assert.Equal(1, verification.ExitCode);
        Assert.False(verification.WorkerResultPresent);
        Assert.Equal(VerificationTerminalContractStatus.Missing,
            WorkerResultBlockers.AssessSubscriptionVerification(fixture.Task, verification).Status);
        Assert.Equal(failureKind, verification.ProviderFailureKind);
        var outcome = DispatchFailureClassifier.Classify(fixture.Task, verification);
        Assert.Equal(outcomeKind, outcome.Kind);
        Assert.Equal(recovery, outcome.RecoveryRecommendation);
        Assert.Contains($"rule={rule};", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.NotEqual(DispatchOutcomeKind.ProviderBudgetExhausted, outcome.Kind);
        Assert.DoesNotContain("rule=provider-budget-exhausted", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void CompletedRound_Quoted402InStdout_RemainsContractFailure(AgentRole role)
    {
        using var fixture = new Fixture(role);
        var output = $"The test fixture quotes this diagnostic: {GrokBudgetDiagnostic}";

        var verification = fixture.CompleteRound(output, string.Empty);

        Assert.Equal(ProviderFailureKind.Unknown, verification.ProviderFailureKind);
        Assert.False(verification.WorkerResultPresent);
        Assert.Equal(VerificationTerminalContractStatus.Missing,
            WorkerResultBlockers.AssessSubscriptionVerification(fixture.Task, verification).Status);
        var outcome = DispatchFailureClassifier.Classify(fixture.Task, verification);
        Assert.Equal(DispatchOutcomeKind.VerificationIncomplete, outcome.Kind);
        Assert.Contains("rule=verification-terminal-contract-incomplete", outcome.ClassifierReceipt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("rule=provider-budget-exhausted", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        Assert.Single(RepairEntries(fixture));
    }

    [Fact]
    public void CompletedRound_ReportedTestFailure_RemainsTaskFailure()
    {
        using var fixture = new Fixture(AgentRole.Tester);
        const string output = """
            WORKER_RESULT:
            files: none
            commands: isolated fixture check
            tests: fail - fixture assertion expected 2 but got 1
            commit: none
            blockers: assertion failed in the candidate
            findings: []
            touched_anchors: []
            model_fit: xAI/fixture - adequate - isolated verification - fixture
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;

        var verification = fixture.CompleteRound(output, string.Empty);

        Assert.Equal(1, verification.ExitCode);
        Assert.True(verification.WorkerResultPresent);
        Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(fixture.Task, verification).Status);
        Assert.Equal(ProviderFailureKind.Unknown, verification.ProviderFailureKind);
        var outcome = DispatchFailureClassifier.Classify(fixture.Task, verification);
        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Contains("rule=tester-worker-result-blocker;", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.DoesNotContain("rule=provider-budget-exhausted", outcome.ClassifierReceipt, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.Empty(RepairEntries(fixture));
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Tester)]
    public void RecordedBudgetRound_PriorRepair_DoesNotSpendAnotherRepair(AgentRole role)
    {
        using var fixture = new Fixture(role);
        fixture.RecordRound("Introduction only.");

        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        var seededRepair = Assert.Single(RepairEntries(fixture));
        Assert.StartsWith($"{role} terminal contract repair 1/2; same-role retry:", seededRepair.Message);
        var seededVerification = Assert.Single(fixture.Task.VerificationHistory);
        Assert.Equal(ProviderFailureKind.Unknown, seededVerification.ProviderFailureKind);
        Assert.Equal("verification-terminal-contract-incomplete", seededVerification.CompletionVerdictRule);
        Assert.Equal(VerificationTerminalContractStatus.Missing,
            WorkerResultBlockers.AssessStoredSubscriptionVerification(fixture.Task, seededVerification).Status);
        var seededRepairCount = RepairEntries(fixture).Count();
        var timelineCount = fixture.Goal.Timeline.Count;

        fixture.RecordRound(string.Empty, GrokBudgetDiagnostic, ProviderFailureKind.BudgetExhausted);

        // A stale or duplicate completion must not make the no-new-repair assertion pass vacuously.
        Assert.Equal(2, fixture.Task.VerificationHistory.Count);
        Assert.Same(seededVerification, fixture.Task.VerificationHistory[0]);
        Assert.Equal("Introduction only.", fixture.Task.VerificationHistory[0].StandardOutput);
        Assert.Equal("verification-terminal-contract-incomplete",
            fixture.Task.VerificationHistory[0].CompletionVerdictRule);
        var budgetVerification = fixture.Task.VerificationHistory[^1];
        Assert.Same(budgetVerification, fixture.Task.LastVerification);
        Assert.Equal(ProviderFailureKind.BudgetExhausted, budgetVerification.ProviderFailureKind);
        Assert.Equal(1, budgetVerification.ExitCode);
        Assert.Equal(GrokBudgetDiagnostic, budgetVerification.StandardError);
        Assert.False(budgetVerification.WorkerResultPresent);
        Assert.Equal(VerificationTerminalContractStatus.Missing,
            WorkerResultBlockers.AssessSubscriptionVerification(fixture.Task, budgetVerification).Status);
        Assert.Equal("provider-budget-exhausted", budgetVerification.CompletionVerdictRule);
        var outcome = DispatchFailureClassifier.Classify(
            fixture.Task, budgetVerification, budgetVerification.ProviderFailureKind);
        Assert.Equal(DispatchOutcomeKind.ProviderBudgetExhausted, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);

        Assert.Equal(seededRepairCount, RepairEntries(fixture).Count());
        Assert.Same(seededRepair, Assert.Single(RepairEntries(fixture)));
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
        var budgetEvents = fixture.Goal.Timeline.Skip(timelineCount)
            .Where(entry => entry.TaskId == fixture.Task.Id).ToArray();
        Assert.DoesNotContain(budgetEvents, entry => entry.Kind == ProgressKind.TaskRetried);
        Assert.DoesNotContain(budgetEvents, entry =>
            entry.Message.Contains("terminal contract repair", StringComparison.Ordinal));
        var failure = Assert.Single(budgetEvents, entry => entry.Kind == ProgressKind.TaskFailed);
        Assert.StartsWith("Dispatch failed: rule=provider-budget-exhausted", failure.Message);
    }

    private static IEnumerable<ProgressEvent> RepairEntries(Fixture fixture) =>
        fixture.Goal.Timeline.Where(entry => entry.TaskId == fixture.Task.Id &&
            entry.Kind == ProgressKind.TaskRetried &&
            entry.Message.Contains("terminal contract repair", StringComparison.Ordinal));

    private sealed class Fixture : IDisposable
    {
        public Fixture(AgentRole role)
        {
            Root = CreateTempDirectory();
            Kernel = new AgentOrchestratorKernel(Clock);
            Task = new TaskSpec(TaskId.New(), "Verify candidate", role);
            Goal = Kernel.CreateGoal("Verify provider budget routing", [Task]);
            var agent = new AgentDefinition(AgentId.New(), role.ToString(), role,
                new ModelProfile("xAI", "fixture", ModelCapability.Text, SubscriptionMode.ApiKey));
            Kernel.ActivateGoal(Goal.Id, [agent]);
        }

        public string Root { get; }
        public FixtureClock Clock { get; } = new();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Task { get; }

        public void BeginRound()
        {
            Clock.Advance();
            Kernel.RecordTaskDispatch(Goal.Id, Task.Id,
                new TaskDispatchRecord("grok-cli", "grok review", Root, Clock.UtcNow,
                    WorkerProviderKind: ProviderKind.XaiGrokCli));
            Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Running, "Fixture attempt started.");
        }

        public void RecordRound(string output, string error = "",
            ProviderFailureKind failureKind = ProviderFailureKind.Unknown)
        {
            BeginRound();
            Clock.Advance();
            var dispatch = Task.LastDispatch!;
            var verification = new TaskVerificationRecord(dispatch.Command, Root, 1, output, error,
                Clock.UtcNow, ProviderFailureKind: failureKind,
                DispatchStartedAt: dispatch.DispatchedAt, EvidenceSource: VerificationEvidenceSource.Dispatch,
                DispatchAttemptAt: dispatch.DispatchedAt);
            var previousCount = Task.VerificationHistory.Count;
            Kernel.RecordDispatchExecutionResult(Goal.Id, Task.Id, verification);
            Assert.Equal(previousCount + 1, Task.VerificationHistory.Count);
            Assert.Equal(Clock.UtcNow, Task.VerificationHistory[^1].CompletedAt);
        }

        // Shared completion seam for the Tester-owned provider parsing and routing cases.
        // The exit artifact, process identity and logs all belong to the same dispatch attempt.
        public TaskVerificationRecord CompleteRound(string output, string error, int exitCode = 1)
        {
            BeginRound();
            var dispatch = Task.LastDispatch!;
            var attemptRoot = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attemptRoot);
            var stdout = Path.Combine(attemptRoot, "worker.out.log");
            var stderr = Path.Combine(attemptRoot, "worker.err.log");
            var exit = Path.Combine(attemptRoot, "worker.exit.txt");
            File.WriteAllText(stdout, output);
            File.WriteAllText(stderr, error);
            File.WriteAllText(exit, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Kernel.RecordTaskProcessStarted(Goal.Id, Task.Id,
                new TaskProcessRecord(4246, dispatch.Command, Root, stdout, stderr, exit,
                    dispatch.DispatchedAt, null, null));
            Clock.Advance();
            var previousCount = Task.VerificationHistory.Count;
            new BackgroundDispatchRunner(clock: Clock, isStillRunning: _ => false,
                disableProcessStart: true, tryKillOwnedProcess: _ =>
                    throw new InvalidOperationException("Completion fixture must not kill a process."))
                .RefreshLatestProcess(Kernel, Goal.Id, Task.Id);
            Assert.Equal(previousCount + 1, Task.VerificationHistory.Count);
            return Task.VerificationHistory[^1];
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public void Advance() => UtcNow = UtcNow.AddSeconds(1);
    }
}
