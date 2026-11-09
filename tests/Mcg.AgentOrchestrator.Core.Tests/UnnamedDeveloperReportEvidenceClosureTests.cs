using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: every scenario owns an in-memory kernel and no external resources.
public sealed class UnnamedDeveloperReportEvidenceClosureTests
{
    private const string UnnamedCriterion = "Developer owns; Reviewer executes. The Developer reports " +
        "`tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added.";
    private const string NamedCriterion = "The Developer reports `tests: deferred - AlphaTests, BetaTests`.";
    private const string MatchingTests = "deferred - AlphaTests, BetaTests";
    private static readonly DateTimeOffset DispatchTime = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MatchingTests)]
    [InlineData("deferred - AlphaTests")]
    public void UnnamedReportClosesFromPassingReviewAndQuotesRecordedTests(string testsField)
    {
        var scenario = CreateScenario(testsField);

        RecordReviewer(scenario);

        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.True(scenario.Request.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, scenario.Request.AuthoritativeAnswer!.Origin);
        Assert.Equal(scenario.Goal.AuthoritativeBrief.Version, scenario.Request.AuthoritativeAnswer.BriefVersion);
        Assert.StartsWith($"DEVELOPER_REPORT_EVIDENCE task={scenario.Developer.Id.Value}", scenario.Request.Answer);
        Assert.Contains($"reviewer_task={scenario.Reviewer.Id.Value}", scenario.Request.Answer);
        Assert.Contains("candidate=B", scenario.Request.Answer);
        Assert.Contains($"tests={testsField}", scenario.Request.Answer);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.StartsWith("REVIEWER_VERDICT_EVIDENCE_SKIPPED"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-run")]
    [InlineData("pass - AlphaTests, BetaTests")]
    [InlineData("deferred - ")]
    public void MissingNonDeferredOrClasslessReportStaysOpen(string? testsField)
    {
        var scenario = CreateScenario(testsField);

        RecordReviewer(scenario);

        AssertSkipped(scenario, "developer-report-missing");
    }

    [Theory]
    [InlineData(UnnamedCriterion)]
    [InlineData(NamedCriterion)]
    public void ExplicitNotMetCannotCloseThroughEitherReportPath(string criterion)
    {
        var rejected = CreateScenario(criterion: criterion);
        var notes = new List<string>();
        var closure = new ReviewerVerdictEvidenceClosure(new object(), new FakeClock(),
            (_, _, _, message) => notes.Add(message));
        // Direct closure bypasses attestation, which would reject not-met before closure.
        closure.Close(rejected.Goal, rejected.Reviewer, ReviewVerification("not-met"), [rejected.Request]);

        Assert.False(rejected.Request.IsCompleted);
        Assert.Null(rejected.Request.Answer);
        Assert.Equal($"REVIEWER_VERDICT_EVIDENCE_SKIPPED request={rejected.Request.Id.Value[..8]} reason=verdict-not-met",
            Assert.Single(notes));

        var control = CreateScenario(criterion: criterion);
        closure.Close(control.Goal, control.Reviewer, ReviewVerification("not-verifiable"), [control.Request]);
        Assert.True(control.Request.IsCompleted);
        Assert.Contains($"tests={MatchingTests}", control.Request.Answer);
    }

    [Theory]
    [InlineData("deferred - BetaTests, AlphaTests", true)]
    [InlineData("deferred - AlphaTests", false)]
    [InlineData("deferred - AlphaTests, BetaTests, GammaTests", false)]
    public void NamedCriterionStillRequiresTheExactClassSet(string testsField, bool closes)
    {
        var scenario = CreateScenario(testsField, NamedCriterion);

        RecordReviewer(scenario);

        if (closes)
        {
            Assert.True(scenario.Request.IsCompleted);
            Assert.Contains($"tests={testsField}", scenario.Request.Answer);
        }
        else AssertSkipped(scenario, "developer-report-differs");
    }

    [Fact]
    public void LaterNamedLiteralCannotBeBypassedByAnEmptyLiteral()
    {
        var scenario = CreateScenario(criterion: "Report `tests: deferred - ` then `tests: deferred - AlphaTests, BetaTests`.");

        RecordReviewer(scenario);

        AssertSkipped(scenario, "verdict-not-met");
    }

    [Theory]
    [InlineData("reviewer")]
    [InlineData("worker")]
    [InlineData("tester")]
    public void OtherWaitOwnersCannotUseTheUnnamedDeveloperReport(string owner)
    {
        var scenario = CreateScenario(owner: owner);

        RecordReviewer(scenario);

        AssertSkipped(scenario, "verdict-not-met");
    }

    private static Scenario CreateScenario(string? testsField = MatchingTests, string criterion = UnnamedCriterion,
        string owner = "developer")
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan evidence", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Close unnamed report evidence", [planner, developer, reviewer]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Bind evidence to candidate", [criterion],
            VerificationClass.TestVerifiable, [], []) { AcceptanceGateOwnedAcceptanceCriteria = [criterion] });
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, developer.Id,
            new TaskDispatchRecord("fixture", "implement", "C:\\fixture", DispatchTime, BaseCommit: "A"));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "B");
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: src/Candidate.cs", "commands: implement",
            "tests: " + (testsField ?? MatchingTests), "commit: B", "blockers: none", "assigned_scope_complete: true",
            "model_fit: fixture/model - adequate - implementation", "skills: none", "confidence: high", "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
            "implement", "C:\\fixture", 0, output, string.Empty, DispatchTime.AddSeconds(1),
            WorkerResultPresent: true, HasCommittedChanges: true, DispatchStartedAt: DispatchTime));
        if (testsField is null)
        {
            // Model a historical stored dispatch report with no tests field, preserving its source.
            var missingTestsOutput = output.Replace($"tests: {MatchingTests}{Environment.NewLine}", string.Empty);
            developer.RecordVerification(developer.LastVerification! with
            {
                StandardOutput = missingTestsOutput,
                AuthoritativeStandardOutput = missingTestsOutput
            });
        }
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal("B", developer.LastDispatch!.ResultCommit);
        Assert.Equal(VerificationEvidenceSource.Dispatch, developer.LastVerification!.EvidenceSource);
        if (testsField is null)
            Assert.False(WorkerResultBlockers.TryFindTests(developer.LastVerification.StandardOutput, out _));

        var identity = new PlannerEvidenceIdentity(goal.Id.Value, planner.Id.Value,
            goal.AuthoritativeBrief.Version, 1, "developer-report");
        var payload = new PlannerEvidencePayload(1, "developer-report", "prospective", owner,
            "Recorded tests field", "Worker reports; Acceptance executes", null, null, null);
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, planner.Id, "Provide report evidence",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner,
            plannerIdentity: identity, plannerEvidence: payload).Request;
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "fixture", "review", "C:\\fixture", DispatchTime.AddSeconds(3), BaseCommit: "B"));
        return new Scenario(kernel, goal, developer, reviewer, request);
    }

    private static TaskVerificationRecord ReviewVerification(string verdict)
    {
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - GateProofTests",
            "commit: none", "blockers: none", "findings: []", "touched_anchors: []",
            "criteria_verdicts: [{\"criterion_index\":0,\"verdict\":\"" + verdict + "\",\"evidence\":\"Recorded Developer report\"}]",
            "verdict: pass", "model_fit: fixture/model - adequate - review", "skills: none",
            "confidence: high", "END_WORKER_RESULT");
        return new TaskVerificationRecord("review", "C:\\fixture", 0, output, string.Empty,
            DispatchTime.AddSeconds(4), WorkerResultPresent: true, ReviewedCommit: "B",
            DispatchStartedAt: DispatchTime.AddSeconds(3));
    }

    private static void RecordReviewer(Scenario scenario) =>
        scenario.Kernel.RecordDispatchExecutionResult(scenario.Goal.Id, scenario.Reviewer.Id,
            ReviewVerification("not-verifiable"));

    private static void AssertSkipped(Scenario scenario, string reason)
    {
        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.False(scenario.Request.IsCompleted);
        Assert.Null(scenario.Request.Answer);
        Assert.Null(scenario.Request.AuthoritativeAnswer);
        var skipped = Assert.Single(scenario.Goal.Timeline.Where(item =>
            item.Message.StartsWith("REVIEWER_VERDICT_EVIDENCE_SKIPPED", StringComparison.Ordinal)));
        Assert.Equal($"REVIEWER_VERDICT_EVIDENCE_SKIPPED request={scenario.Request.Id.Value[..8]} reason={reason}", skipped.Message);
        Assert.Same(scenario.Request, Assert.Single(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id)));
    }

    private sealed record Scenario(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Developer,
        TaskSpec Reviewer, HumanInputRequest Request);
}
