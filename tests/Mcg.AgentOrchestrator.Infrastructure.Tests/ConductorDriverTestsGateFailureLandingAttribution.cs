using System.Text.Json;
using System.Xml.Linq;
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ConsecutiveSameLaneTimeoutRoutesToOperatorWithoutWorkerDispatch(
        bool priorTimeoutHasFailingTest, bool hasRecordedDetail)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = VerifiedGoal();
            var attempt = 0;
            string? escalation = null;
            var policy = ConductorAutonomyPolicy.Conservative with { AutomaticRetryOperatorRoutingHoldSeconds = 0 };
            var firstTimeout = priorTimeoutHasFailingTest ? TimeoutCheckWithFailingIdentity() : TimeoutCheck();
            const string secondTimeout = "acceptance-check-timeout: focused-reverse-dependent-infrastructure-tests elapsed=42m budget=42m";
            const string detailPrefix = "LANE_TIMEOUT_DETAIL lane=focused-reverse-dependent-infrastructure-tests ";
            var secondCheck = TimeoutCheck() with { Name = secondTimeout };
            var conductLog = Path.Combine(root, ConductEventLogWriter.CurrentFileName);
            string? unrelatedTrx = null;
            if (hasRecordedDetail)
            {
                var firstTrx = WriteTrx(root, "lane-first.trx",
                    ("Ns.Slow", "00:00:10", "Passed"), ("Ns.Beta", "00:00:06.250", "Passed"),
                    ("Ns.Ignored", "00:09:00", "NotExecuted"));
                var secondTrx = WriteTrx(root, "lane-second.trx",
                    ("Ns.Slow", "00:00:02.500", "Passed"), ("Ns.Alpha", "00:00:06.250", "Passed"),
                    ("Ns.Fourth", "00:00:01", "Passed"));
                var malformed = Path.Combine(root, "malformed.trx");
                File.WriteAllText(malformed, "<TestRun>");
                unrelatedTrx = WriteTrx(root, "unrelated.trx", ("Ns.Unrelated", "00:09:00", "Passed"));
                firstTimeout = firstTimeout with { TestResultPaths = [unrelatedTrx] };
                secondCheck = secondCheck with
                {
                    TestResultPaths = [firstTrx, secondTrx, firstTrx, Path.Combine(root, "missing.trx"), malformed],
                    TestResultAttemptId = "timeout-attempt-2"
                };
                WriteTimeoutLoad(root, conductLog, goal.Id);
            }
            var driver = MakeDriver(
                executionDirectory: root,
                runAcceptanceSummary: candidate => RecordTimeout(root, candidate, ++attempt,
                    attempt == 1 ? firstTimeout : secondCheck) with
                {
                    TestResultPaths = unrelatedTrx is null ? null : [unrelatedTrx]
                },
                retryTaskWithCause: (goalId, taskId, message, _, cause) =>
                {
                    Assert.True(priorTimeoutHasFailingTest);
                    Assert.Equal(1, attempt);
                    Assert.Equal(AgentRole.Developer, goal.FindTask(taskId).RequiredRole);
                    Assert.Contains(Identity, message);
                    return kernel.RetryTask(goalId, taskId, message, cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                dispatchAndStart: _ => throw new InvalidOperationException("Timeout must not dispatch a worker."),
                recordTaskNote: (goalId, taskId, note) => kernel.RecordTaskNote(goalId, taskId, note),
                writeEscalation: (_, _, reason) => escalation = reason);
            driver.LaneTimeoutConductEventLogPathOverride = conductLog;

            var first = driver.AdvanceOnce(goal, policy).Outcome;
            if (priorTimeoutHasFailingTest)
            {
                Assert.IsType<ConductorAdvanceOutcome.Executed>(first);
                Assert.DoesNotContain(goal.Timeline, evt => evt.Message.StartsWith("ACCEPTANCE_TIMEOUT_REGATE:", StringComparison.Ordinal));
                CompleteTasksAtSameCandidate(kernel, goal);
            }
            else
            {
                Assert.IsType<ConductorAdvanceOutcome.Held>(first);
            }
            Assert.Null(escalation);
            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(
                driver.AdvanceOnce(goal, policy).Outcome);

            var decision = Assert.IsType<PolicyDecisionRecord>(escalated.Decision);
            Assert.Equal("Escalate", decision.Action);
            Assert.Equal("consecutive-same-lane-timeout", decision.DiscriminatingEvidence);
            Assert.Contains(new PolicyDecisionFact("previousAttemptId", "timeout-attempt-1"), decision.Facts);
            Assert.Contains(new PolicyDecisionFact("attemptId", "timeout-attempt-2"), decision.Facts);
            var reason = Assert.IsType<string>(escalation);
            Assert.Contains($"attempt timeout-attempt-1 ({TimeoutCheckName})", reason);
            Assert.Contains($"attempt timeout-attempt-2 ({secondTimeout})", reason);
            var expectedDetail = detailPrefix + (hasRecordedDetail
                ? "slowest=[Ns.Slow 12.5s, Ns.Alpha 6.25s, Ns.Beta 6.25s] trx_unavailable=[trx-missing:missing.trx, trx-unparseable:malformed.trx] " +
                  "host_available_memory=8192 MiB concurrent_gates=3"
                : "slowest=unavailable(no-trx-paths) host_available_memory=unavailable(no-lane-receipt-store) " +
                  "concurrent_gates=unavailable(no-conduct-event-log)");
            Assert.EndsWith("\n" + expectedDetail, reason);
            Assert.Equal(reason, escalated.Reason);
            Assert.Equal(reason, decision.Reason);
            Assert.Contains(new PolicyDecisionFact("timeoutLaneDetail", expectedDetail), decision.Facts);
            Assert.Equal(2, attempt);
            Assert.Equal(priorTimeoutHasFailingTest ? 1 : 0, goal.AutomaticAcceptanceRetryCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CancelledGateBetweenSameLaneTimeoutsPreservesDeveloperRetry()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = VerifiedGoal();
            var attempt = 1;
            var driver = MakeDriver(
                executionDirectory: root,
                runAcceptanceSummary: candidate => RecordTimeout(root, candidate, attempt, TimeoutCheck()),
                retryTaskWithCause: (goalId, taskId, message, _, cause) =>
                {
                    Assert.Equal(3, attempt);
                    Assert.Equal(AgentRole.Developer, goal.FindTask(taskId).RequiredRole);
                    Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                    Assert.Contains(TimeoutCheckName, message);
                    return kernel.RetryTask(goalId, taskId, message, cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                recordTaskNote: (goalId, taskId, note) => kernel.RecordTaskNote(goalId, taskId, note),
                writeEscalation: (_, _, _) => throw new InvalidOperationException("The cancellation breaks the timeout sequence."));

            Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
            GoalOperationJournal.AcceptanceBlocked(root, goal, "conductor:acceptance", "disposition-cancelled",
                _repository.MergeSha, _repository.MainSha, attemptStartedAt: DateTimeOffset.UnixEpoch.AddHours(2));
            attempt = 3;

            Assert.IsType<ConductorAdvanceOutcome.Executed>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
            Assert.Equal(1, goal.AutomaticAcceptanceRetryCount);
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
            string? retryMessage = null;
            var driver = MakeDriver(
                executionDirectory: root,
                runAcceptanceSummary: candidate => RecordTimeout(root, candidate, 1, TimeoutCheckWithFailingIdentity()),
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

    private static AcceptanceCheckResult TimeoutCheckWithFailingIdentity() => TimeoutCheck() with
    {
        FailingTestIdentities = [Identity],
        OutputTail = $"[FAIL] {Identity}"
    };

    private static string WriteTrx(string root, string fileName, params (string Class, string Duration, string Outcome)[] tests)
    {
        var path = Path.Combine(root, fileName);
        new XElement("TestRun",
            new XElement("TestDefinitions", tests.Select((test, index) =>
                new XElement("UnitTest", new XAttribute("id", index),
                    new XElement("TestMethod", new XAttribute("className", test.Class))))),
            new XElement("Results", tests.Select((test, index) =>
                new XElement("UnitTestResult", new XAttribute("testId", index),
                    new XAttribute("duration", test.Duration), new XAttribute("outcome", test.Outcome))))).Save(path);
        return path;
    }

    private static void WriteTimeoutLoad(string root, string conductLog, GoalId goalId)
    {
        const string lane = "Focused reverse dependent infrastructure tests";
        var receiptPath = GateLaneReceiptLedger.ResolveStorePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        var writer = new ConductEventLogWriter(conductLog);
        WriteSample(lane, DateTimeOffset.UnixEpoch.AddHours(2).AddMinutes(42), 8192, 3);
        WriteSample(lane, DateTimeOffset.UnixEpoch.AddHours(1).AddMinutes(40), 512, 7);
        WriteSample("Unrelated lane", DateTimeOffset.UnixEpoch.AddHours(2).AddMinutes(43), 1024, 9);

        void WriteSample(string target, DateTimeOffset at, int memory, int gateCount)
        {
            SharedJsonlFile.AppendLine(receiptPath, JsonSerializer.Serialize(new
            {
                goal_id = goalId.Value, lane = target, recorded_at = at, host_available_memory_mb = memory
            }));
            var sample = GateLoadSample.Available(gateCount);
            var progress = new AcceptanceGateProgress(goalId.Value, "shard-complete", target, null, null, null,
                at.AddMinutes(-40), at, at, TimeSpan.FromMinutes(40), 0, "fixture heartbeat",
                new GateLoadContext(sample, sample, sample, sample, sample, sample), Outcome: "failed");
            writer.Append("gate-progress", goalId.Value[..8], GateProgressConductEvents.Format(progress), at);
        }
    }

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
        CompleteTasksAtSameCandidate(kernel, goal);
        return (kernel, goal);
    }

    private void CompleteTasksAtSameCandidate(AgentOrchestratorKernel kernel, Goal goal)
    {
        foreach (var task in goal.Tasks.Where(task => task.Status != WorkTaskStatus.Completed))
        {
            DispatchTask(kernel, goal, task, workingDirectory: _repository.Root, baseCommit: _repository.AnchorSha);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "verify", _repository.Root, 0, "ok", "", DateTimeOffset.Parse("2026-10-15T12:00:00Z"),
                HasCommittedChanges: task.RequiredRole == AgentRole.Developer, ReviewedCommit: _repository.AnchorSha));
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
        }
        Assert.Equal(_repository.AnchorSha, GateFailureLandingAttributor.SelectLastPassingCandidateSha(goal));
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
