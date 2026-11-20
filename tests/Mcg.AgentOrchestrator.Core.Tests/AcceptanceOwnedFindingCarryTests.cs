using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class AcceptanceOwnedFindingCarryTests
{
    private const string Candidate = "candidate-a";
    private const string StableId = "unrelated-cleanup-failure";
    private const string Request = """{"selections":[{"test_project":"tests/Other.Tests/Other.Tests.csproj","test_class":"OtherCleanupTests"}]}""";

    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void PassingScopeCarriesFindingWithoutWorkerFailure(AgentRole role)
    {
        var (kernel, goal, task) = Record(role);

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.True(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
        Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.TaskNote && item.TaskId == task.Id &&
            item.Message == $"ACCEPTANCE_FINDING_CARRIED stable_id={StableId}; candidate_sha={Candidate}");
        Assert.Equal([StableId], AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));
        Assert.DoesNotContain(goal.Timeline, item => item.Kind is ProgressKind.TaskFailed or ProgressKind.TaskRetried);
        Assert.All(goal.CriterionEvidenceObligations, item => Assert.Equal(CriterionEvidenceOwner.Worker, item.Owner));
    }

    [Fact]
    public void GatePassClosesCarryWithReceiptAndSurvivesRecovery()
    {
        var (kernel, goal, _) = Record();
        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        Assert.Equal([StableId], AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));

        kernel.RecordCarriedAcceptanceFindingGateOutcome(goal.Id, Candidate, passed: true, source: "attempt:gate-1");
        var receipt = Assert.Single(goal.Timeline.Where(item =>
            item.Message.StartsWith("ACCEPTANCE_FINDING_CLOSED ", StringComparison.Ordinal)));
        Assert.Null(receipt.TaskId);
        Assert.Contains($"stable_id={StableId}; candidate_sha={Candidate}; receipt=full-acceptance:{Candidate}", receipt.Message);
        Assert.Contains("attempt:gate-1", receipt.Message);
        Assert.Empty(AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));

        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        kernel.RecordCarriedAcceptanceFindingGateOutcome(goal.Id, Candidate, passed: true, source: "attempt:gate-2");
        Assert.Single(goal.Timeline.Where(item => item.Message.StartsWith("ACCEPTANCE_FINDING_CLOSED ", StringComparison.Ordinal)));
        Assert.Empty(AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));
    }

    [Fact]
    public void OtherCandidateCannotSettleCarry()
    {
        var (kernel, goal, _) = Record();
        kernel.RecordCarriedAcceptanceFindingGateOutcome(goal.Id, "candidate-b", passed: true, source: "attempt:other");
        Assert.Equal([StableId], AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));
        Assert.DoesNotContain(goal.Timeline, item => item.Message.StartsWith("ACCEPTANCE_FINDING_CLOSED ", StringComparison.Ordinal));
    }

    [Fact]
    public void GateFailureRetainsFindingAndReportsItsIdWithoutWorkerRetry()
    {
        var (kernel, goal, task) = Record();
        kernel.RecordAcceptanceFailure(goal.Id, ["OtherCleanupTests failed"], Candidate, "main-a");

        Assert.Equal([StableId], AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));
        Assert.Contains(goal.Timeline, item => item.TaskId is null &&
            item.Message.Contains($"Acceptance gate failed for carried finding: stable_id={StableId}; candidate_sha={Candidate}", StringComparison.Ordinal));
        Assert.Equal(ReviewFindingState.Open, Assert.Single(task.LastVerification!.MergedReviewFindings!).State);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.DoesNotContain(goal.Timeline, item => item.Kind is ProgressKind.TaskRetried or ProgressKind.TaskFailed);

        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        goal = kernel.GetGoal(goal.Id);
        Assert.Equal([StableId], AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));
        kernel.RecordCarriedAcceptanceFindingGateOutcome(goal.Id, Candidate, passed: true, source: "attempt:retry-gate");
        Assert.Empty(AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));
    }

    [Theory]
    [InlineData("correctness", Request)]
    [InlineData("acceptance-owned", null)]
    [InlineData("acceptance-owned", "{\"selections\":[]}")]
    [InlineData("acceptance-owned", "{\"selections\":[null]}")]
    [InlineData("acceptance-owned", "{\"selections\":null}")]
    [InlineData("acceptance-owned", "{\"selections\":[{\"test_project\":\"tests/Other.Tests/Other.Tests.csproj\",\"test_class\":\" \"}]}")]
    [InlineData("acceptance-owned", "{\"selections\":[{\"test_project\":\" \",\"test_class\":\"OtherCleanupTests\"}]}")]
    [InlineData("future-unknown-category", Request)]
    public void IneligibleFindingRetainsExistingTesterRejection(string category, string? request)
    {
        var (_, goal, task) = Record(category: category, request: request);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed &&
            item.Message == Rejection(StableId));
        Assert.Empty(AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));
    }

    [Fact]
    public void MixedFindingsCarryEligibleFindingAndRejectOwnScope()
    {
        var (_, goal, task) = Record(mixed: true);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal([StableId], AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));
        Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed && item.Message == Rejection("code-defect"));
    }

    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void CarryDoesNotOverrideFailedCriterion(AgentRole role)
    {
        var (kernel, goal, task) = Record(role, criterionVerdict: "not-met");
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.False(kernel.BuildVerificationGate(goal.Id).IsSatisfied);
    }

    [Fact]
    public void OperatorMappedDeferralRetainsPrecedence()
    {
        var (_, goal, task) = Record(mapped: true);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Contains(goal.Timeline, item => item.Message.Contains($"Deferred acceptance-owned finding remains pending: stable_id={StableId}", StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, item => item.Message.StartsWith("ACCEPTANCE_FINDING_CARRIED ", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingCandidateCannotGrantCarry()
    {
        var (_, goal, task) = Record(candidate: null);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.TaskFailed && item.Message == Rejection(StableId));
    }

    [Fact]
    public void TaskNoteAndEmbeddedWorkerTextCannotCloseCarry()
    {
        var (kernel, goal, task) = Record();
        kernel.RecordTaskNote(goal.Id, task.Id,
            AcceptanceOwnedFindingCarry.ClosureNote(StableId, Candidate, "worker-claim"));
        kernel.RecordTaskNote(goal.Id, task.Id, "Worker description: " +
            AcceptanceOwnedFindingCarry.ClosureNote(StableId, Candidate, "embedded-claim"));
        Assert.Equal([StableId], AcceptanceOwnedFindingCarry.GetOpenCarried(goal, Candidate));
    }

    private static string Rejection(string id) =>
        $"Tester WORKER_RESULT rejected: merged structured finding state still has open blocking stable_id(s): {id}; automatic ownership routing required.";

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) Record(
        AgentRole role = AgentRole.Tester, string category = "acceptance-owned", string? request = Request,
        bool mixed = false, string criterionVerdict = "met", bool mapped = false, string? candidate = Candidate)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Inspect current candidate", role);
        var goal = kernel.CreateGoal("Carry unrelated acceptance evidence", [task]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Verify source", ["Source handles the failure path"],
            VerificationClass.TestVerifiable, [], []));
        if (mapped)
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance, "operator",
                findingStableId: StableId, expectedCandidateSha: Candidate);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("fixture", "review", "C:\\fixture",
            DateTimeOffset.UtcNow, BaseCommit: candidate));
        var finding = Finding(StableId, category, request);
        if (mixed) finding += "," + Finding("code-defect", "correctness", Request);
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: fail - unrelated cleanup failed",
            "commit: none", "blockers: none", $"findings: [{finding}]", "touched_anchors: []",
            "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"" + criterionVerdict + "\",\"evidence\":\"Source verified\"}]",
            "verdict: needs-work", "model_fit: fixture/model - adequate - deterministic control", "skills: none",
            "confidence: high", "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord("review", "C:\\fixture",
            0, output, string.Empty, DateTimeOffset.UtcNow, WorkerResultPresent: true));
        return (kernel, goal, task);
    }

    private static string Finding(string stableId, string category, string? request) =>
        "{\"stable_id\":" + JsonSerializer.Serialize(stableId) +
        ",\"state\":\"open\",\"location\":{\"file\":\"tests/OtherCleanupTests.cs\",\"region\":" + JsonSerializer.Serialize(stableId) +
        "},\"description\":\"Acceptance cleanup requires a full gate receipt.\",\"severity\":\"blocking\",\"category\":" +
        JsonSerializer.Serialize(category) + (request is null ? "" : ",\"evidence_request\":" + request) + "}";
}
