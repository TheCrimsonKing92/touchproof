using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: a unique fixture repository per class, and unique index/prompt directories per fact.
// PrepareTask writes a brief only; no worker or test process is started by these facts.
public sealed class ConductorDriverTestsGateFailureLandingAttribution :
    IClassFixture<GateFailureLandingAttributorTests.RepositoryFixture>
{
    private const string Identity = "Ns.RebindReplayProbe.Case";
    private const string Header = "Failing class attribution (candidate diff and main history only; no new test run):";
    private const string TimeoutCheckName =
        "acceptance-check-timeout: focused-reverse-dependent-infrastructure-tests elapsed=40m budget=40m";
    private readonly GateFailureLandingAttributorTests.RepositoryFixture _repository;

    public ConductorDriverTestsGateFailureLandingAttribution(GateFailureLandingAttributorTests.RepositoryFixture repository) =>
        _repository = repository;

    [Fact]
    public void RetryRecordsAttributionAndPublishesItInDeveloperBrief()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = VerifiedGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            string? retryMessage = null;
            string? promptPath = null;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(FailingCheck()),
                retryTaskWithCause: (goalId, taskId, message, _, cause) =>
                {
                    Assert.Equal(developer.Id, taskId);
                    Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                    retryMessage = message;
                    var task = kernel.RetryTask(goalId, taskId, message, cause);
                    promptPath = WorkerProfileDispatcher.PrepareTask(
                        kernel, goal, task,
                        new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory} {promptPath}"),
                        Path.Combine(root, "prompts"), _repository.Root, DateTimeOffset.UtcNow,
                        providerName: "OpenAI", modelName: AgentCatalog.OpenAiSubscriptionModelAlias).PromptPath;
                    return task;
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Driver.cs"],
                apparatusRedGate: CreateGate(root));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            var message = Assert.IsType<string>(retryMessage);
            Assert.StartsWith("Acceptance criteria unmet; retrying task with feedback (attempt 1/", message);
            Assert.Contains(Header, message);
            Assert.Contains(InteractionLine(), message);
            Assert.True(message.IndexOf(Header, StringComparison.Ordinal) > message.IndexOf("Concrete acceptance failure evidence", StringComparison.Ordinal));
            Assert.Contains(developer.CriterionRetryFeedback, line => line.Contains(InteractionLine(), StringComparison.Ordinal));
            var prompt = File.ReadAllText(Assert.IsType<string>(promptPath));
            Assert.Contains("## Unmet acceptance criteria from the prior attempt - fix these:", prompt);
            Assert.Contains(InteractionLine(), prompt);
            Assert.Equal(1, goal.AutomaticAcceptanceRetryCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SpentRetryBudgetAppendsAttributionToEscalation()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (_, goal) = VerifiedGoal();
            string? escalation = null;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(FailingCheck()),
                retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Spent budget must not retry."),
                writeEscalation: (_, _, reason) => escalation = reason,
                getLandingFileScopes: _ => ["src/Driver.cs"],
                apparatusRedGate: CreateGate(root));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative with { MaxCriterionRetries = 0 });

            Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
            var reason = Assert.IsType<string>(escalation);
            Assert.Contains("Acceptance criteria unmet after", reason);
            Assert.Contains("; " + Header + "; " + InteractionLine(), reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GateWithoutAttributorPreservesRetryMessageWithoutHeader()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = VerifiedGoal();
            string? retryMessage = null;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(FailingCheck()),
                retryTaskWithCause: (goalId, taskId, message, _, cause) =>
                {
                    retryMessage = message;
                    return kernel.RetryTask(goalId, taskId, message, cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Driver.cs"],
                apparatusRedGate: CreateGate(root, includeAttributor: false));

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var message = Assert.IsType<string>(retryMessage);
            Assert.StartsWith("Acceptance criteria unmet; retrying task with feedback (attempt 1/", message);
            Assert.DoesNotContain(Header, message);
            Assert.DoesNotContain("likely interaction", message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TimeoutOnlyGateFailureRegatesUnchangedCandidateWithoutDeveloperDispatch()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = VerifiedGoal();
            var gatedCandidates = new List<(GoalId Goal, string? ReviewedCommit)>();
            var driver = MakeDriver(
                executionDirectory: root,
                runAcceptanceSummary: candidate =>
                {
                    gatedCandidates.Add((candidate.Id, GateFailureLandingAttributor.SelectLastPassingCandidateSha(candidate)));
                    return gatedCandidates.Count == 1
                        ? RecordTimeout(root, candidate, 1, TimeoutCheck())
                        : AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Timeout must not retry a worker."),
                dispatchAndStart: _ => throw new InvalidOperationException("Timeout must not dispatch a worker."),
                recordTaskNote: (goalId, taskId, note) => kernel.RecordTaskNote(goalId, taskId, note));

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

            Assert.Equal(GoalLifecycleState.Verified, held.State);
            var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
            Assert.Equal("Hold", decision.Action);
            Assert.Equal("first-timeout-on-candidate", decision.DiscriminatingEvidence);
            Assert.Contains(new PolicyDecisionFact("attemptId", "timeout-attempt-1"), decision.Facts);
            Assert.Single(gatedCandidates);
            Assert.Contains(goal.Timeline, evt => evt.Kind == ProgressKind.TaskNote &&
                evt.Message.Contains("attempt=timeout-attempt-1", StringComparison.Ordinal) &&
                evt.Message.Contains(TimeoutCheckName, StringComparison.Ordinal));

            var next = Assert.IsType<ConductorAdvanceOutcome.Executed>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

            Assert.StartsWith("Landed:", next.Description);
            Assert.Equal([(goal.Id, _repository.AnchorSha), (goal.Id, _repository.AnchorSha)], gatedCandidates);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ConsecutiveSameLaneTimeoutRoutesToOperatorWithoutWorkerDispatch()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = VerifiedGoal();
            var attempt = 0;
            string? escalation = null;
            const string secondTimeout = "acceptance-check-timeout: focused-reverse-dependent-infrastructure-tests elapsed=42m budget=42m";
            var driver = MakeDriver(
                executionDirectory: root,
                runAcceptanceSummary: candidate => RecordTimeout(root, candidate, ++attempt,
                    attempt == 1 ? TimeoutCheck() : TimeoutCheck() with { Name = secondTimeout }),
                retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Timeout must not retry a worker."),
                dispatchAndStart: _ => throw new InvalidOperationException("Timeout must not dispatch a worker."),
                recordTaskNote: (goalId, taskId, note) => kernel.RecordTaskNote(goalId, taskId, note),
                writeEscalation: (_, _, reason) => escalation = reason);

            Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
            Assert.Null(escalation);
            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

            var decision = Assert.IsType<PolicyDecisionRecord>(escalated.Decision);
            Assert.Equal("Escalate", decision.Action);
            Assert.Equal("consecutive-same-lane-timeout", decision.DiscriminatingEvidence);
            Assert.Contains(new PolicyDecisionFact("previousAttemptId", "timeout-attempt-1"), decision.Facts);
            Assert.Contains(new PolicyDecisionFact("attemptId", "timeout-attempt-2"), decision.Facts);
            var reason = Assert.IsType<string>(escalation);
            Assert.Contains($"attempt timeout-attempt-1 ({TimeoutCheckName})", reason);
            Assert.Contains($"attempt timeout-attempt-2 ({secondTimeout})", reason);
            Assert.Equal(2, attempt);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TimeoutWithFailingTestIdentityStillRetriesDeveloperWithFeedback()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = VerifiedGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var timeout = TimeoutCheck() with
            {
                FailingTestIdentities = [Identity],
                OutputTail = $"[FAIL] {Identity}"
            };
            string? retryMessage = null;
            var driver = MakeDriver(
                executionDirectory: root,
                runAcceptanceSummary: candidate => RecordTimeout(root, candidate, 1, timeout),
                retryTaskWithCause: (goalId, taskId, message, _, cause) =>
                {
                    Assert.Equal(developer.Id, taskId);
                    Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                    retryMessage = message;
                    return kernel.RetryTask(goalId, taskId, message, cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

            var message = Assert.IsType<string>(retryMessage);
            Assert.StartsWith("Acceptance criteria unmet; retrying task with feedback (attempt 1/", message);
            Assert.Contains(TimeoutCheckName, message);
            Assert.Contains(Identity, message);
            Assert.Equal(1, goal.AutomaticAcceptanceRetryCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static AcceptanceCheckResult TimeoutCheck() => new(TimeoutCheckName, false, -1, null);

    private AcceptanceVerificationSummary RecordTimeout(
        string root, Goal goal, int ordinal, params AcceptanceCheckResult[] checks)
    {
        var summary = RedSummary(checks) with { AttemptId = $"timeout-attempt-{ordinal}" };
        GoalOperationJournal.AcceptanceBlocked(root, goal, "conductor:acceptance", "timeout",
            summary.BranchHeadSha, summary.MainHeadSha,
            attemptStartedAt: DateTimeOffset.UnixEpoch.AddHours(ordinal), failedCheckNames: summary.FailedChecks);
        return summary;
    }

    private (AgentOrchestratorKernel Kernel, Goal Goal) VerifiedGoal()
    {
        var (kernel, goal) = SoftwareGoal();
        foreach (var task in goal.Tasks)
        {
            DispatchTask(kernel, goal, task, workingDirectory: _repository.Root, baseCommit: _repository.AnchorSha);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "verify", _repository.Root, 0, "ok", "", DateTimeOffset.Parse("2026-10-15T12:00:00Z"),
                HasCommittedChanges: task.RequiredRole == AgentRole.Developer, ReviewedCommit: _repository.AnchorSha));
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
        }
        Assert.Equal(_repository.AnchorSha, GateFailureLandingAttributor.SelectLastPassingCandidateSha(goal));
        return (kernel, goal);
    }

    private ApparatusRedGate CreateGate(string root, bool includeAttributor = true) => new(
        CreateIndex(root), _ => _repository.Root, () => DateTimeOffset.Parse("2026-10-15T12:30:00Z"),
        landingAttributor: includeAttributor ? new GateFailureLandingAttributor(_repository.Root) : null);

    private static AcceptanceFailingTestIndex CreateIndex(string root) =>
        new(Path.Combine(root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));

    private static AcceptanceCheckResult FailingCheck() => new(
        "focused infrastructure tests", false, 1, "Assert.Equal() Failure: expected rebound candidate",
        FailingTestIdentities: [Identity],
        FailingTestAttributions: [new AcceptanceTestFailureAttribution(
            Identity, AcceptanceTestFailureOrigin.Introduced, "focused identity was green at merge-base")]);

    private AcceptanceVerificationSummary RedSummary(params AcceptanceCheckResult[] unmet) => new(
        false, unmet,
        FailedChecks: unmet.Where(check => !check.Passed).Select(check => check.Name).ToArray(),
        BranchHeadSha: _repository.MergeSha,
        MainHeadSha: _repository.MainSha,
        CheckAttributions: unmet.Select(check => new AcceptanceCheckAttribution(
            check.Name, AcceptanceFailureOrigin.Introduced, "main is attested green")).ToArray());

    private string InteractionLine() =>
        $"RebindReplayProbe: added by goal 6a4ec362 (commit {_repository.LandingSha[..9]}) on main after this candidate's last pass at {_repository.AnchorSha[..8]}; likely interaction with that landing.";
}
