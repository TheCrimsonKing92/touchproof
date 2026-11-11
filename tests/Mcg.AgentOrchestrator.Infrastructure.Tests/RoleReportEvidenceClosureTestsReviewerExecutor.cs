using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: scenarios own in-memory kernels and perform no real I/O.
public sealed class RoleReportEvidenceClosureTestsReviewerExecutor
{
    internal const string ReportCriterion = "The Developer report states the measurement method, phase boundaries, per-phase numbers and chosen remedy. Developer owns; Reviewer executes. TEST-VERIFIABLE.";
    private static readonly DateTimeOffset DispatchTime = new(2026, 11, 5, 17, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("B")]
    [InlineData("A")]
    public void ReviewerExecutedReport_PassingCurrentRound_ClosesWait(string reviewed)
    {
        var (kernel, goal, reviewer, request) = CreateScenario(reviewed: reviewed);
        var rangeChecks = 0;

        var skipped = kernel.CloseRoleReportEvidence(goal.Id, "C",
            _ => throw new Exception("Reviewer execution must not query deferred class evidence"),
            conductorOnlyRange: (from, to) =>
            {
                Assert.Equal("A", from);
                Assert.Equal("C", to);
                rangeChecks++;
                return true;
            });

        Assert.Equal(reviewed == "A" ? 1 : 0, rangeChecks);
        Assert.Empty(skipped);
        Assert.True(request.IsCompleted);
        Assert.StartsWith($"ROLE_REPORT_EVIDENCE owner=developer task={reviewer.Id.Value} criterion=1 ", request.Answer);
        Assert.Contains($"candidate={reviewed} landing_candidate=C", request.Answer);
        Assert.Contains("worker_result_log=reviewer.out.log", request.Answer);
        Assert.EndsWith(" verdict=pass executor=reviewer", request.Answer);
        Assert.Equal(HumanInputAnswerOrigin.Worker, request.AuthoritativeAnswer!.Origin);
        Assert.Equal(goal.AuthoritativeBrief.Version, request.AuthoritativeAnswer.BriefVersion);
        Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(request.Answer,
            Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived)).Message);
    }

    [Theory]
    [InlineData("pass", "exact-blocker - fixture", "B", WorkTaskStatus.Completed, "owner-round-incomplete")]
    [InlineData("needs-work", "none", "B", WorkTaskStatus.Completed, "owner-round-incomplete")]
    [InlineData("pass", "none", "B", WorkTaskStatus.Failed, "owner-round-incomplete")]
    [InlineData("pass", "none", "A", WorkTaskStatus.Completed, "owner-candidate-differs")]
    [InlineData("pass", "none", "", WorkTaskStatus.Completed, "owner-candidate-differs")]
    public void ReviewerExecutedReport_UnqualifiedRound_KeepsWait(string verdict, string blockers,
        string reviewed, WorkTaskStatus status, string reason)
    {
        var (kernel, goal, _, request) = CreateScenario(verdict: verdict, blockers: blockers,
            reviewed: reviewed, status: status);
        var rangeChecks = 0;

        var skipped = kernel.CloseRoleReportEvidence(goal.Id, "C",
            _ => throw new Exception("Reviewer execution must not query gate classes"),
            conductorOnlyRange: (from, to) =>
            {
                Assert.Equal("A", from);
                Assert.Equal("C", to);
                rangeChecks++;
                return false;
            });

        Assert.Equal(reviewed == "A" ? 1 : 0, rangeChecks);
        AssertPending(kernel, goal, request, skipped, reason);
    }

    [Theory]
    [InlineData("Developer owns; Tester executes.", "tester")]
    [InlineData("Developer owns; Acceptance executes. TEST-VERIFIABLE.", "acceptance")]
    [InlineData("The Reviewer confirms the report.", "missing")]
    [InlineData("Developer owns; Reviewer executes. Tester owns; Acceptance executes.", "ambiguous")]
    [InlineData("Developer owns; Reviewer executes. Developer owns; Reviewer executes.", "ambiguous")]
    [InlineData("Judge owns; Reviewer executes.", "other-role")]
    [InlineData("Developer owns; Judge executes.", "other-role")]
    public void ReportWithoutReviewerExecutor_KeepsWaitWithTypedReason(string criterion, string outcome)
    {
        var (kernel, goal, _, request) = CreateScenario(criterion: criterion);

        var skipped = kernel.CloseRoleReportEvidence(goal.Id, "C",
            _ => throw new Exception("Non-deferred report must not query gate classes"),
            conductorOnlyRange: (_, _) => throw new Exception("No Reviewer executor was declared"));

        AssertPending(kernel, goal, request, skipped, $"no-executor-closes-report:{outcome}");
    }

    [Fact]
    public void ReviewerExecutedReport_NewerFailedRound_DoesNotReusePassingRound()
    {
        var (kernel, goal, older, request) = CreateScenario();
        var later = kernel.AddTask(goal.Id, AgentRole.Reviewer, "Review revision", AgentCatalog.Default().Agents);
        RecordReviewer(kernel, goal, later, "needs-work", "none", "B", WorkTaskStatus.Completed,
            DispatchTime.AddMinutes(1));
        Assert.True(WorkerResultBlockers.TryFindPassVerdict(older.LastVerification!));

        var skipped = kernel.CloseRoleReportEvidence(goal.Id, "C", _ => null,
            conductorOnlyRange: (_, _) => throw new Exception("Latest round did not pass"));

        AssertPending(kernel, goal, request, skipped, "owner-round-incomplete");
    }

    [Fact]
    public void DeferredReport_WithReviewerExecutor_StillRequiresGateClasses()
    {
        var criterion = RoleReportEvidenceClosureTests.ReportCriterion.Replace(
            "Acceptance executes", "Reviewer executes", StringComparison.Ordinal);
        var (kernel, goal, _, request) = CreateScenario(criterion: criterion);
        var queried = new List<string>();

        var skipped = kernel.CloseRoleReportEvidence(goal.Id, "C", name =>
        {
            queried.Add(name);
            return null;
        });

        Assert.Equal(new[] { "GateProofTests" }, queried);
        AssertPending(kernel, goal, request, skipped, "class-not-in-passed-gate:GateProofTests");
    }

    [Fact]
    public void ReviewerExecutedReport_DifferentBrief_KeepsWait()
    {
        var (kernel, goal, _, request) = CreateScenario(waitVersion: 2);

        var skipped = kernel.CloseRoleReportEvidence(goal.Id, "C", _ => null,
            conductorOnlyRange: (_, _) => throw new Exception("Stale brief must not check ranges"));

        AssertPending(kernel, goal, request, skipped, "brief-version-differs");
    }

    private static void AssertPending(AgentOrchestratorKernel kernel, Goal goal, HumanInputRequest request,
        IReadOnlyDictionary<HumanInputRequestId, string> skipped, string reason)
    {
        Assert.Equal(request.Id, Assert.Single(skipped).Key);
        Assert.Equal(reason, skipped[request.Id]);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Empty(request.AnswerHistory);
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
        Assert.Same(request, Assert.Single(kernel.HumanInputRequests));
        Assert.Equal($"ROLE_REPORT_EVIDENCE_SKIPPED request={request.Id.Value[..8]} reason={reason}",
            Assert.Single(goal.Timeline.Where(item => item.Message.StartsWith(
                "ROLE_REPORT_EVIDENCE_SKIPPED", StringComparison.Ordinal))).Message);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Reviewer, HumanInputRequest Request)
        CreateScenario(string criterion = ReportCriterion, string verdict = "pass", string blockers = "none",
            string reviewed = "B", WorkTaskStatus status = WorkTaskStatus.Completed, int waitVersion = 1)
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan report evidence", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Inspect Developer report", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Close Reviewer-executed Developer reports", [planner, developer, reviewer]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Bind Reviewer evidence",
            [criterion], VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "implement", "C:\\fixture", DispatchTime, BaseCommit: "A"));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "B");
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
            "implement", "C:\\fixture", 0, RoleReportEvidenceClosureTests.DeveloperOutput("deferred - GateProofTests"),
            string.Empty, DispatchTime.AddSeconds(1), WorkerResultPresent: true, HasCommittedChanges: true,
            DispatchStartedAt: DispatchTime));
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        RecordReviewer(kernel, goal, reviewer, verdict, blockers, reviewed, status, DispatchTime.AddSeconds(2));
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, planner.Id, "Provide Developer report",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: "developer",
            plannerIdentity: new PlannerEvidenceIdentity(goal.Id.Value, planner.Id.Value,
                waitVersion, 1, "developer-report"),
            plannerEvidence: new PlannerEvidencePayload(1, "developer-report", "post-implementation", "developer",
                "Developer report", "Reviewer executes", null, null, null)).Request;
        Assert.False(request.IsCompleted);
        return (kernel, goal, reviewer, request);
    }

    private static void RecordReviewer(AgentOrchestratorKernel kernel, Goal goal, TaskSpec reviewer,
        string verdict, string blockers, string reviewed, WorkTaskStatus status, DateTimeOffset started)
    {
        var output = RoleReportEvidenceClosureTests.RoleOutput(verdict, "met")
            .Replace("blockers: none", $"blockers: {blockers}", StringComparison.Ordinal);
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "inspect", "C:\\fixture", started, BaseCommit: reviewed));
        kernel.RecordTaskVerification(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "inspect", "C:\\fixture", 0, output, string.Empty, started.AddSeconds(1),
            StandardOutputPath: "reviewer.out.log", WorkerResultPresent: true));
        // Isolate closure checks from upstream worker-result classification.
        kernel.ReportWorkerTaskProgress(goal.Id, reviewer.Id, status, "Reviewer round adjudicated.");
        Assert.Equal(status, reviewer.Status);
        Assert.Equal(reviewed, reviewer.LastVerification!.ReviewedCommit);
        Assert.Equal(verdict == "pass", WorkerResultBlockers.TryFindPassVerdict(reviewer.LastVerification));
    }
}
