using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each scenario owns an in-memory kernel and uses recorded dispatch times.
public sealed class ReviewerVerdictEvidenceClosureTestsTesterCommit
{
    private const string SkippedPrefix = "REVIEWER_VERDICT_EVIDENCE_SKIPPED";
    private static readonly DateTimeOffset DispatchTime = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("reviewer")]
    [InlineData("tester")]
    public void LaterTesterCommit_MatchingReview_ClosesOwnedWait(string owner)
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario, owner);

        RecordReviewer(scenario, "T");

        AssertClosed(scenario, request, "T", testerCandidate: true);
        if (owner == "tester")
            Assert.Contains($"owner=tester owner_task={scenario.Tester.Id.Value}", request.Answer);
    }

    [Fact]
    public void ForeignTesterBase_ReviewOnTesterCommit_LeavesWaitOpen()
    {
        var scenario = CreateScenario(testerBase: "X");
        var request = CreateWait(scenario);

        RecordReviewer(scenario, "T");

        AssertSkipped(scenario, request, "candidate-differs reviewed=T expected=D");
        AssertPassingReviewClosesControlWait();

        var fallback = CreateScenario(testerBase: "X");
        var fallbackRequest = CreateWait(fallback);
        RecordReviewer(fallback, "D");
        AssertClosed(fallback, fallbackRequest, "D", testerCandidate: false);
    }

    [Fact]
    public void UnrelatedReviewedCommit_LeavesWaitOpenAndNamesExpectedTesterCommit()
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario);

        RecordReviewer(scenario, "X");

        AssertSkipped(scenario, request, "candidate-differs reviewed=X expected=T");
        AssertPassingReviewClosesControlWait();
    }

    [Fact]
    public void NeedsWorkVerdict_OnTesterCommit_LeavesWaitOpen()
    {
        var scenario = CreateScenario();
        var request = CreateWait(scenario);

        RecordReviewer(scenario, "T", finding: true);

        AssertSkipped(scenario, request, "review-not-pass");
        AssertPassingReviewClosesControlWait();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void TesterDispatchNotLater_ReviewOnTesterCommit_LeavesWaitOpen(int offset)
    {
        var scenario = CreateScenario(testerDispatchOffset: offset);
        var request = CreateWait(scenario);

        RecordReviewer(scenario, "T");

        AssertSkipped(scenario, request, "candidate-differs reviewed=T expected=D");
        AssertPassingReviewClosesControlWait();

        var fallback = CreateScenario(testerDispatchOffset: offset);
        var fallbackRequest = CreateWait(fallback);
        RecordReviewer(fallback, "D");
        AssertClosed(fallback, fallbackRequest, "D", testerCandidate: false);
    }

    [Fact]
    public void MissingTesterResult_ReviewOnDeveloperCommit_ClosesWithoutTesterProvenance()
    {
        var scenario = CreateScenario(testerResult: null);
        var request = CreateWait(scenario);

        RecordReviewer(scenario, "D");

        AssertClosed(scenario, request, "D", testerCandidate: false);
        AssertPassingReviewClosesControlWait();
    }

    [Fact]
    public void SameTesterResult_ClosesWithDeveloperProvenance()
    {
        var scenario = CreateScenario(testerResult: "D");
        var request = CreateWait(scenario);

        RecordReviewer(scenario, "D");

        AssertClosed(scenario, request, "D", testerCandidate: true);
    }

    [Theory]
    [InlineData(WorkTaskStatus.Completed, "D", false)]
    [InlineData(WorkTaskStatus.Running, "T", true)]
    [InlineData(WorkTaskStatus.Failed, "T", true)]
    public void NewerInvalidTester_SelectsLatestCompletedThenFallsBack(
        WorkTaskStatus newerStatus, string candidate, bool testerCandidate)
    {
        var scenario = CreateScenario(newerTesterStatus: newerStatus);
        var request = CreateWait(scenario);

        RecordReviewer(scenario, candidate);

        AssertClosed(scenario, request, candidate, testerCandidate);
        AssertPassingReviewClosesControlWait();
    }

    private static void AssertClosed(Scenario scenario, HumanInputRequest request,
        string candidate, bool testerCandidate)
    {
        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.True(request.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, request.AuthoritativeAnswer!.Origin);
        Assert.Equal(scenario.Goal.AuthoritativeBrief.Version, request.AuthoritativeAnswer.BriefVersion);
        Assert.StartsWith("REVIEWER_VERDICT_EVIDENCE task=", request.Answer);
        Assert.Contains($" candidate={candidate} ", request.Answer);
        if (testerCandidate)
            Assert.Contains("developer_candidate=D", request.Answer);
        else
            Assert.DoesNotContain("developer_candidate=", request.Answer);
        var received = Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived));
        Assert.Equal(request.Answer, received.Message);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal));
    }

    private static void AssertSkipped(Scenario scenario, HumanInputRequest request, string reason)
    {
        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Null(request.AuthoritativeAnswer);
        Assert.Equal(request.Id, Assert.Single(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id)).Id);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
        var skipped = Assert.Single(scenario.Goal.Timeline.Where(item => item.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal)));
        Assert.Equal(ProgressKind.TaskNote, skipped.Kind);
        Assert.Equal(scenario.Reviewer.Id, skipped.TaskId);
        Assert.Equal($"{SkippedPrefix} request={request.Id.Value[..8]} reason={reason}", skipped.Message);
    }

    private static void AssertPassingReviewClosesControlWait()
    {
        var control = CreateScenario();
        var request = CreateWait(control);
        RecordReviewer(control, "T");
        AssertClosed(control, request, "T", testerCandidate: true);
    }

    private static Scenario CreateScenario(string testerBase = "D", int testerDispatchOffset = 1,
        string? testerResult = "T", WorkTaskStatus? newerTesterStatus = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan evidence", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Verify candidate", AgentRole.Tester);
        var reviewer = new TaskSpec(TaskId.New(), "Inspect diff", AgentRole.Reviewer);
        var tasks = new List<TaskSpec> { planner, developer, tester, reviewer };
        var newerTester = newerTesterStatus is null ? null : new TaskSpec(TaskId.New(), "Recheck candidate", AgentRole.Tester);
        if (newerTester is not null)
            tasks.Add(newerTester);
        var goal = kernel.CreateGoal("Close evidence waits on a Tester commit", tasks);
        string[] criteria = ["Full gate passes.", "Focused tests pass.", "Diff inspection is complete."];
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            "Bind evidence to the implementation candidate", criteria, VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = [criteria[0], criteria[1]]
        });
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "implement", "C:\\fixture", DispatchTime, BaseCommit: "A"));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "D");
        kernel.ReportWorkerTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Candidate D is ready.");
        RecordTester(tester, testerBase, testerResult, testerDispatchOffset, WorkTaskStatus.Completed);
        if (newerTester is not null)
            RecordTester(newerTester, "X", "U", 2, newerTesterStatus!.Value);
        return new Scenario(kernel, goal, planner, tester, reviewer);

        void RecordTester(TaskSpec task, string baseCommit, string? result, int offset, WorkTaskStatus status)
        {
            var startedAt = DispatchTime.AddSeconds(offset);
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
                "codex-cli", "verify", "C:\\fixture", startedAt, BaseCommit: baseCommit));
            if (result is not null)
                kernel.RecordDispatchResultCommit(goal.Id, task.Id, result);
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "verify", "C:\\fixture", 0, string.Empty, string.Empty, startedAt.AddMilliseconds(1),
                WorkerResultPresent: true, ReviewedCommit: "worker-supplied-candidate"));
            kernel.ReportWorkerTaskProgress(goal.Id, task.Id, status, "Tester round recorded.");
            Assert.Equal(status, task.Status);
            Assert.Equal(baseCommit, task.LastVerification!.ReviewedCommit);
            Assert.Equal(result, task.LastDispatch!.ResultCommit);
        }
    }

    private static HumanInputRequest CreateWait(Scenario scenario, string owner = "reviewer")
    {
        var identity = new PlannerEvidenceIdentity(scenario.Goal.Id.Value, scenario.Planner.Id.Value,
            scenario.Goal.AuthoritativeBrief.Version, 3, "diff-inspection");
        var payload = new PlannerEvidencePayload(3, "diff-inspection", "prospective", owner,
            "Inspect diff", "Worker owns this evidence", null, null, null);
        var request = scenario.Kernel.RequestHumanInputDeduplicated(scenario.Goal.Id, scenario.Planner.Id,
            "Provide the diff inspection evidence", kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
            evidenceOwner: owner, plannerIdentity: identity, plannerEvidence: payload).Request;
        Assert.False(request.IsCompleted);
        return request;
    }

    private static void RecordReviewer(Scenario scenario, string candidate, bool finding = false)
    {
        if (finding)
            scenario.Kernel.MapCriterionEvidenceOwner(scenario.Goal.Id, 0,
                scenario.Goal.AuthoritativeRefinedSpecVersion!.Version, CriterionEvidenceOwner.Acceptance,
                "operator", findingStableId: "gate-proof", expectedCandidateSha: candidate);
        var startedAt = DispatchTime.AddSeconds(3);
        scenario.Kernel.RecordTaskDispatch(scenario.Goal.Id, scenario.Reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", "C:\\fixture", startedAt, BaseCommit: candidate));
        var findings = finding
            ? """[{"stable_id":"gate-proof","state":"open","location":{"file":"docs/operator-runbook.md","region":"gate-proof"},"description":"Full acceptance evidence remains pending.","severity":"blocking","category":"acceptance-owned"}]"""
            : "[]";
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - GateProofTests",
            "commit: none", $"blockers: {(finding ? "Required owner evidence remains pending." : "none")}",
            $"findings: {findings}", "touched_anchors: []",
            "criteria_verdicts: [" +
                """{"criterion_index":0,"verdict":"not-verifiable","evidence":"Full acceptance gate"},""" +
                """{"criterion_index":1,"verdict":"not-verifiable","evidence":"Focused tests"},""" +
                """{"criterion_index":2,"verdict":"met","evidence":"Inspected implementation candidate."}]""",
            $"verdict: {(finding ? "needs-work" : "pass")}",
            "model_fit: fixture/model - adequate - deterministic review",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        scenario.Kernel.RecordDispatchExecutionResult(scenario.Goal.Id, scenario.Reviewer.Id, new TaskVerificationRecord(
            "review", "C:\\fixture", 0, output, string.Empty, startedAt.AddSeconds(1),
            WorkerResultPresent: true, ReviewedCommit: "worker-supplied-candidate", DispatchStartedAt: startedAt));
    }

    private sealed record Scenario(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Planner,
        TaskSpec Tester, TaskSpec Reviewer);
}
