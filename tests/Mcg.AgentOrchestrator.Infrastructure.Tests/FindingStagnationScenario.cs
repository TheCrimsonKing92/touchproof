using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// In-memory replay: no processes, repository, database, or ambient clock is used.
internal sealed class FindingStagnationScenario
{
    internal const string StableId = "historical-cause-missing";
    internal const string Blocker = "exact-blocker - historical-cause-missing: never-recorded timings cannot be recovered by a code change.";
    internal const string FirstDescription = "Cause evidence for candidate abc123def is missing after 23 observations.";
    internal const string SecondDescription = "Cause evidence for candidate def456abc is missing after 32 observations.";
    internal AgentOrchestratorKernel Kernel { get; private set; }
    internal Goal Goal { get; private set; }
    internal TaskSpec Developer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
    internal TaskSpec Tester => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
    internal TaskSpec Reviewer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
    internal string? RetryMessage { get; private set; }
    internal int Dispatches { get; private set; }
    internal int Retries => Goal.Timeline.Count(evt => evt.Kind == ProgressKind.TaskRetried);
    internal HumanInputRequest[] Decisions => Kernel.HumanInputRequests.Where(request =>
        request.GoalId == Goal.Id && request.QuestionFingerprint.StartsWith("finding-stagnation:", StringComparison.Ordinal)).ToArray();
    private readonly ReplayClock _clock = new();
    private readonly AgentRole _findingRole;
    private ConductorDriver _driver;
    private int _candidate;
    private PreDispatchIntegrationReceipt? _integration;

    internal FindingStagnationScenario(AgentRole findingRole = AgentRole.Reviewer)
    {
        _findingRole = findingRole;
        Kernel = new AgentOrchestratorKernel(_clock);
        Goal = GoalLifecycleCommands.CreateAndActivateGoal(Kernel, DefaultAgents(), "Recover the cause evidence");
        Kernel.RecordGoalRefinement(Goal.Id, Spec("Explain the historical cause"));
        CompleteDeveloper();
        _driver = CreateDriver();
    }

    internal static ReviewFinding Finding(string description = FirstDescription, string id = StableId) =>
        new(id, ReviewFindingState.Open, new ReviewFindingLocation("src/Cause.cs", "Cause.Explain"),
            description, FindingSeverity.Blocking, FindingCategory.Correctness);

    internal static RefinedSpec Spec(string criterion) =>
        new("Recover the cause evidence", [criterion], VerificationClass.TestVerifiable, [], []);

    internal void AdvanceClock() => _clock.Step();

    internal void CompleteDeveloper(string blocker = "none")
    {
        Record(Developer, Result(blocker), hasChanges: true, integration: _integration);
        _integration = null;
        // Replay the accepted handoff to verification with its original blocker intact.
        // Completion classification is outside this fixture; a verifying role triggers recovery.
        if (Developer.Status != WorkTaskStatus.Completed)
            Kernel.ReportTaskProgress(Goal.Id, Developer.Id, WorkTaskStatus.Completed, "Replay accepted Developer handoff to verification.");
        if (_findingRole == AgentRole.Reviewer)
            Record(Tester, "ok");
    }

    internal void Raise(string description = FirstDescription, IReadOnlyList<ReviewFindingLocation>? touched = null,
        string? diagnostic = null, IReadOnlyList<ReviewFinding>? findings = null)
    {
        var task = _findingRole == AgentRole.Reviewer ? Reviewer : Tester;
        var raised = findings ?? [Finding(description)];
        var candidate = (++_candidate).ToString("x40");
        var output = Result("Blocking cause evidence remains.", raised, _findingRole == AgentRole.Reviewer);
        Record(task, output, fail: true, candidate: candidate, touched: touched ?? [], diagnostic: diagnostic,
            receipts: [new FindingEvidenceReceipt($"receipt-{_candidate}", candidate,
                new FindingEvidenceRequest([new FindingEvidenceSelection("Infrastructure.Tests", "CauseTests")]),
                true, true, "1 test passed")]);
        Assert.Null(task.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(task.LastVerification, out var round, out _));
        Assert.Contains(round.Findings, finding => finding.StableId == raised[0].StableId);
        if (_findingRole == AgentRole.Tester)
            Assert.Same(Developer, TesterFindingDeveloperTargetPolicy.Select(Goal, task));
    }

    internal void Advance()
    {
        RetryMessage = null;
        Dispatches = 0;
        _clock.Step();
        _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
    }

    internal void AssertRetried(int before)
    {
        // One routed Developer retry also invalidates any completed/failed downstream work.
        var retries = Goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried).Skip(before).ToArray();
        var routedRetry = Assert.Single(retries.Where(evt => evt.TaskId == Developer.Id));
        Assert.Equal(RetryMessage, routedRetry.Message);
        Assert.All(retries.Where(evt => evt.TaskId != Developer.Id), evt =>
        {
            Assert.True(evt.TaskId == Tester.Id || evt.TaskId == Reviewer.Id);
            Assert.StartsWith("Invalidated ", evt.Message);
        });
        Assert.Equal(1, Dispatches);
        Assert.StartsWith("auto-review-retry round", RetryMessage);
        Assert.Contains("convergence brief", RetryMessage);
        Assert.DoesNotContain(Decisions, request => !request.IsCompleted);
    }

    internal HumanInputRequest BlockSecondRound(string blocker = Blocker)
    {
        Raise();
        var before = Retries;
        Advance();
        AssertRetried(before);
        CompleteDeveloper(blocker);
        Raise(SecondDescription);
        before = Retries;
        Advance();
        AssertHeld(before);
        return Assert.Single(Decisions);
    }

    internal void AssertHeld(int before)
    {
        Assert.Equal(before, Retries);
        Assert.Equal(0, Dispatches);
        Assert.Null(RetryMessage);
        Assert.Single(Decisions.Where(request => !request.IsCompleted));
    }

    internal void IntegrateMain()
    {
        _clock.Step();
        Kernel.RetryTask(Goal.Id, Developer.Id, "Integrate current main before verification.", RetryCause.MainDriftConflict);
        var receipt = new PreDispatchIntegrationReceipt(Goal.Id, Developer.Id, Developer.LatestRetryAt!.Value,
            new string('a', 40), new string('b', 40), new string('c', 40));
        Kernel.RecordPreDispatchIntegrationReceipt(Goal.Id, Developer.Id, receipt);
        Assert.Equal(receipt, Developer.PendingPreDispatchIntegrationReceipt);
        _integration = receipt;
    }

    internal void Restore()
    {
        Kernel = AgentOrchestratorKernel.FromSnapshot(Kernel.ExportSnapshot(), _clock);
        Goal = Kernel.GetGoal(Goal.Id);
        _driver = CreateDriver();
    }

    private ConductorDriver CreateDriver() => new(
        getFacts: _ => GoalLifecycleFacts.None,
        getRunningPaidWorkerCount: () => 0,
        createWorkspace: _ => "C:\\replay",
        dispatchAndStart: _ => { Dispatches++; return DispatchStartOutcome.Started(); },
        startRecordedDispatches: null,
        buildServerShutdown: null,
        runAcceptanceVerification: _ => throw new InvalidOperationException("Acceptance is outside this retry replay."),
        runAdvisorySemanticAcceptance: null,
        retryTask: (gid, tid, message) => Retry(gid, tid, message, null),
        recordTaskNote: (gid, tid, message) => Kernel.RecordTaskNote(gid, tid, message),
        recordCriterionRetryFeedback: null,
        clearCriterionRetryFeedback: null,
        rebaseOntoMain: _ => new GoalWorktreeRebaseResult(GoalWorktreeRebaseStatus.AlreadyFastForwardable,
            "goal/replay", "Already fast-forwardable", [], null),
        land: (_, _) => throw new InvalidOperationException("A blocked replay must never land."),
        afterSuccessfulLanding: null,
        record: _ => { },
        cleanup: _ => new GoalWorktreeRemoveResult("Replay", null, [], null),
        writeEscalation: (_, _, _) => { },
        classifyChangeRisk: _ => null,
        retryTaskWithRoundKind: (gid, tid, message, kind) => Retry(gid, tid, message, kind),
        retryTaskWithCause: (gid, tid, message, kind, _) => Retry(gid, tid, message, kind),
        findingStagnationKernel: Kernel);

    private TaskSpec Retry(GoalId gid, TaskId tid, string message, RetryRoundKind? kind)
    {
        Assert.Equal(Developer.Id, tid);
        RetryMessage = message;
        return Kernel.RetryTask(gid, tid, message, retryRoundKind: kind);
    }

    private void Record(TaskSpec task, string output, bool fail = false, bool hasChanges = false,
        string? candidate = null, IReadOnlyList<ReviewFindingLocation>? touched = null, string? diagnostic = null,
        IReadOnlyList<FindingEvidenceReceipt>? receipts = null, PreDispatchIntegrationReceipt? integration = null)
    {
        _clock.Step();
        Kernel.RecordTaskDispatch(Goal.Id, task.Id, new TaskDispatchRecord("test-worker", "replay", "C:\\replay",
            _clock.UtcNow, BaseCommit: candidate, ReviewFindingTouchedAnchors: touched,
            ReviewFindingTouchProofDiagnostic: diagnostic, PreDispatchIntegrationReceipt: integration));
        // Bind the Developer's changed output to the candidate the next verifying round reviews.
        // A HasCommittedChanges flag alone cannot establish ownership of a reviewed SHA.
        if (hasChanges)
            Kernel.RecordDispatchResultCommit(Goal.Id, task.Id, (_candidate + 1).ToString("x40"));
        _clock.Step();
        var record = new TaskVerificationRecord("replay", "C:\\replay", fail ? 1 : 0, output, "", _clock.UtcNow,
            WorkerResultPresent: output.Contains("WORKER_RESULT:", StringComparison.Ordinal),
            HasCommittedChanges: hasChanges, FindingEvidenceReceipts: receipts);
        if (fail) Kernel.RecordDispatchExecutionResult(Goal.Id, task.Id, record);
        else Kernel.RecordTaskVerification(Goal.Id, task.Id, record);
    }

    private static string Result(string blocker, IReadOnlyList<ReviewFinding>? findings = null, bool reviewer = false) =>
        "WORKER_RESULT:\nfiles: src/Cause.cs\ncommands: replay\ntests: pass - 1 test passed\n" +
        $"blockers: {blocker}\n" +
        (findings is null ? "" : $"findings: {JsonSerializer.Serialize(findings)}\ntouched_anchors: []\n") +
        (reviewer ? "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"not-met\",\"evidence\":\"Cause evidence is missing.\"}]\nverdict: needs-work\n" : "") + "END_WORKER_RESULT";

    private sealed class ReplayClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        internal void Step() => UtcNow = UtcNow.AddSeconds(1);
    }
}
