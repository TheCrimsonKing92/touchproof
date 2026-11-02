using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its in-memory kernel; no shared resources or real I/O.
public sealed class RoleReportEvidenceClosureTestsTesterResult
{
    private static readonly DateTimeOffset DispatchTime = new(2026, 11, 1, 23, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pass - candidate B; GateProofTests 3/3")]
    [InlineData("pass")]
    public void TesterResult_PassedCurrentCandidate_ClosesWithTestsMarker(string tests)
    {
        var (kernel, goal, task, request) = CreateScenario(tests);

        var skipped = kernel.CloseRoleReportEvidence(goal.Id, "C",
            _ => throw new Exception("Tester evidence must not query gate classes"),
            conductorOnlyRange: (_, _) => throw new Exception("Matching candidate needs no range check"));

        Assert.Empty(skipped);
        Assert.True(request.IsCompleted);
        Assert.StartsWith($"ROLE_REPORT_EVIDENCE owner=tester task={task.Id.Value}", request.Answer);
        Assert.Contains("candidate=B landing_candidate=C", request.Answer);
        Assert.EndsWith(" tests=pass", request.Answer);
        Assert.Equal(HumanInputAnswerOrigin.Worker, request.AuthoritativeAnswer!.Origin);
        Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(request.Answer,
            Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived)).Message);
    }

    [Theory]
    [InlineData("fail - GateProofTests 1/3", "none", AgentRole.Tester, WorkTaskStatus.Completed)]
    [InlineData("pass - GateProofTests 3/3", "exact-blocker - fixture", AgentRole.Tester, WorkTaskStatus.Completed)]
    [InlineData("deferred - GateProofTests", "none", AgentRole.Tester, WorkTaskStatus.Completed)]
    [InlineData("pass - GateProofTests 3/3", "none", AgentRole.Reviewer, WorkTaskStatus.Completed)]
    [InlineData("pass - GateProofTests 3/3", "none", AgentRole.Tester, WorkTaskStatus.Failed)]
    [InlineData("inconclusive - host unavailable", "none", AgentRole.Tester, WorkTaskStatus.Completed)]
    [InlineData("not-run - host unavailable", "none", AgentRole.Tester, WorkTaskStatus.Completed)]
    [InlineData("pass", "", AgentRole.Tester, WorkTaskStatus.Completed)]
    public void RoleResult_IneligibleWithoutVerdict_KeepsWait(string tests, string blockers,
        AgentRole role, WorkTaskStatus status)
    {
        var (kernel, goal, _, request) = CreateScenario(tests, blockers, role, status: status);

        var skipped = kernel.CloseRoleReportEvidence(goal.Id, "C", _ => null,
            conductorOnlyRange: (_, _) => throw new Exception("Incomplete rounds must not check candidate ranges"));

        Assert.Equal("owner-round-incomplete", skipped[request.Id]);
        Assert.False(request.IsCompleted);
        Assert.Null(request.Answer);
        Assert.Empty(request.AnswerHistory);
        Assert.Contains(request, kernel.GetPendingHumanInput(goal.Id));
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TesterResult_DifferentCandidate_RequiresConductorOnlyRange(bool conductorOnly)
    {
        var (kernel, goal, _, request) = CreateScenario("pass - GateProofTests 3/3", candidate: "A");
        var rangeChecks = 0;

        var skipped = kernel.CloseRoleReportEvidence(goal.Id, "C", _ => null,
            conductorOnlyRange: (reviewed, landing) =>
            {
                Assert.Equal("A", reviewed);
                Assert.Equal("C", landing);
                rangeChecks++;
                return conductorOnly;
            });

        Assert.Equal(1, rangeChecks);
        if (conductorOnly)
        {
            Assert.Empty(skipped);
            Assert.True(request.IsCompleted);
            Assert.Contains("candidate=A landing_candidate=C", request.Answer);
            Assert.EndsWith(" tests=pass", request.Answer);
        }
        else
        {
            Assert.Equal("owner-candidate-differs", skipped[request.Id]);
            Assert.False(request.IsCompleted);
            Assert.Null(request.Answer);
            Assert.Contains(request, kernel.GetPendingHumanInput(goal.Id));
            Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
        }
    }

    [Fact]
    public void TesterResult_AlsoHasPassVerdict_PreservesVerdictAnswer()
    {
        var (kernel, goal, _, request) = CreateScenario("pass", verdict: "pass");

        var skipped = kernel.CloseRoleReportEvidence(goal.Id, "C", _ => null);

        Assert.Empty(skipped);
        Assert.True(request.IsCompleted);
        Assert.StartsWith("ROLE_REPORT_EVIDENCE owner=tester", request.Answer);
        Assert.DoesNotContain("tests=pass", request.Answer);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, HumanInputRequest Request)
        CreateScenario(string tests, string blockers = "none", AgentRole role = AgentRole.Tester,
            string candidate = "B", WorkTaskStatus status = WorkTaskStatus.Completed, string? verdict = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan evidence", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var task = new TaskSpec(TaskId.New(), "Verify candidate", role);
        var goal = kernel.CreateGoal("Close completed role reports", [planner, developer, task]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Bind role evidence",
            ["A completed role round supplies evidence."], VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "implement", "C:\\fixture", DispatchTime, BaseCommit: "A"));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "B");
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
            "implement", "C:\\fixture", 0, RoleReportEvidenceClosureTests.DeveloperOutput("deferred - GateProofTests"),
            string.Empty, DispatchTime.AddSeconds(1), WorkerResultPresent: true, HasCommittedChanges: true,
            DispatchStartedAt: DispatchTime));
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);

        var started = DispatchTime.AddSeconds(2);
        var output = RoleOutput(tests, blockers);
        if (verdict is not null)
            output = output.Replace("END_WORKER_RESULT", $"verdict: {verdict}{Environment.NewLine}END_WORKER_RESULT");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "verify", "C:\\fixture", started, BaseCommit: candidate));
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "verify", "C:\\fixture", 0, output, string.Empty, started.AddSeconds(1), WorkerResultPresent: true));
        // Adjudicate status so result parsing, rather than upstream classification, controls closure.
        kernel.ReportWorkerTaskProgress(goal.Id, task.Id, status, "Role round adjudicated.");
        Assert.Equal(status, task.Status);
        Assert.Equal(candidate, task.LastVerification!.ReviewedCommit);
        Assert.Equal(verdict == "pass", WorkerResultBlockers.TryFindPassVerdict(task.LastVerification));
        if (verdict is null) Assert.DoesNotContain("verdict:", output);

        var owner = role.ToString().ToLowerInvariant();
        var key = $"report-{owner}";
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, planner.Id, $"Provide {key}",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner,
            plannerIdentity: new PlannerEvidenceIdentity(goal.Id.Value, planner.Id.Value,
                goal.AuthoritativeBrief.Version, 1, key),
            plannerEvidence: new PlannerEvidencePayload(1, key, "post-implementation", owner,
                "Role report", "Role owns; Acceptance executes", null, null, null)).Request;
        Assert.False(request.IsCompleted);
        return (kernel, goal, task, request);
    }

    private static string RoleOutput(string tests, string blockers) => string.Join(Environment.NewLine,
        "WORKER_RESULT:", "files: none", "commands: verify", $"tests: {tests}", "commit: none",
        $"blockers: {blockers}", "model_fit: fixture/model - adequate - deterministic verification",
        "skills: none", "confidence: high", "END_WORKER_RESULT");
}
