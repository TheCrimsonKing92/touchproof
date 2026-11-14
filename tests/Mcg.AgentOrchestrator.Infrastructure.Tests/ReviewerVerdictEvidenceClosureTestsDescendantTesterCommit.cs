using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private kernels, clocks and runners; the live adapter test owns a disposable git repository.
// No test changes the process-wide ancestry registration.
public sealed class ReviewerVerdictEvidenceClosureTestsDescendantTesterCommit
{
    private const string Skipped = "REVIEWER_VERDICT_EVIDENCE_SKIPPED";
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("reviewer")]
    [InlineData("tester")]
    [InlineData("developer")]
    [InlineData("worker")]
    public void SecondTesterRound_DescendantReview_ClosesOwnedWait(string owner)
    {
        var scenario = CreateScenario(owner);
        var calls = 0;

        RecordReviewer(scenario, "T", (ancestor, descendant) =>
        {
            calls++;
            Assert.Equal("D", ancestor);
            Assert.Equal("T", descendant);
            return true;
        });

        Assert.Equal(1, calls);
        AssertClosed(scenario, "T", developerCandidate: true);
        Assert.Equal("T", scenario.Tester.LastDispatch!.BaseCommit);
        Assert.Equal("T", scenario.Tester.LastDispatch.ResultCommit);
        Assert.Equal("T", scenario.Tester.LastVerification!.ReviewedCommit);
        if (owner == "tester")
            Assert.Contains($"owner=tester owner_task={scenario.Tester.Id.Value}", scenario.Request.Answer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeOrThrowingAncestry_PreservesCandidateDiffers(bool throws)
    {
        var scenario = CreateScenario();
        var calls = 0;
        RecordReviewer(scenario, "T", (_, _) =>
        {
            calls++;
            if (throws) throw new IOException("lookup unavailable");
            return false;
        });

        Assert.Equal(1, calls);
        AssertSkipped(scenario, "candidate-differs reviewed=T expected=D");
    }

    [Fact]
    public void MissingPredicate_PreservesCandidateDiffers()
    {
        var scenario = CreateScenario();
        var verification = RecordReviewer(scenario, "T", (_, _) => false);
        var notes = new List<string>();

        new ReviewerVerdictEvidenceClosure(new object(), new FixedClock(), (_, _, _, message) => notes.Add(message))
            .Close(scenario.Goal, scenario.Reviewer, verification, [scenario.Request]);

        Assert.False(scenario.Request.IsCompleted);
        Assert.Equal($"{Skipped} request={scenario.Request.Id.Value[..8]} reason=candidate-differs reviewed=T expected=D",
            Assert.Single(notes));
    }

    [Theory]
    [InlineData("unrelated", "candidate-differs reviewed=X expected=D")]
    [InlineData("not-later", "candidate-differs reviewed=T expected=D")]
    [InlineData("non-pass", "review-not-pass")]
    [InlineData("missing-result", "candidate-differs reviewed=T expected=D")]
    public void IneligibleReview_DoesNotConsultAncestry(string shape, string reason)
    {
        var scenario = CreateScenario(secondTesterOffset: shape == "not-later" ? 0 : 2,
            testerResult: shape == "missing-result" ? null : "T");
        var calls = 0;
        RecordReviewer(scenario, shape == "unrelated" ? "X" : "T", (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("ineligible review reached ancestry");
        }, pass: shape != "non-pass");

        Assert.Equal(0, calls);
        AssertSkipped(scenario, reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingExactPaths_DoNotConsultAncestry(bool exactTesterBase)
    {
        var scenario = CreateScenario(secondTesterBase: exactTesterBase ? "D" : "T");
        var calls = 0;
        RecordReviewer(scenario, exactTesterBase ? "T" : "D", (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("exact path reached ancestry");
        });

        Assert.Equal(0, calls);
        AssertClosed(scenario, exactTesterBase ? "T" : "D", developerCandidate: exactTesterBase);
    }

    [Theory]
    [InlineData(0, false, true, true)]
    [InlineData(1, false, true, false)]
    [InlineData(128, false, true, false)]
    [InlineData(0, true, true, false)]
    [InlineData(0, false, false, false)]
    public void Adapter_RequiresSuccessfulStartedAndDrainedGit(int exit, bool timedOut, bool started, bool expected)
    {
        var calls = 0;
        string? observedRoot = null;
        string[]? observedArguments = null;
        var memo = new GitAncestryMemo(4, (root, args) =>
        {
            calls++;
            observedRoot = root;
            observedArguments = args;
            return new GitCli.GitResult(exit, "", "", timedOut, started);
        });

        Assert.Equal(expected, memo.IsReviewerEvidenceAncestor("fixture-root", " a40e4d77 ", "949904e4"));
        Assert.Equal(1, calls);
        Assert.Equal("fixture-root", observedRoot);
        Assert.Equal(new[] { "merge-base", "--is-ancestor", "a40e4d77", "949904e4" }, observedArguments);
    }

    [Fact]
    public void Adapter_ThrowingRunnerAnswersNo()
    {
        var calls = 0;
        var memo = new GitAncestryMemo(4, (_, _) => { calls++; throw new IOException("git unavailable"); });

        Assert.False(memo.IsReviewerEvidenceAncestor("fixture-root", "a40e4d77", "949904e4"));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("D")]
    [InlineData("HEAD")]
    [InlineData("--help")]
    public void Adapter_InvalidObjectIdDoesNotRunGit(string operand)
    {
        var calls = 0;
        var memo = new GitAncestryMemo(4, (_, _) => { calls++; return new(0, "", ""); });

        Assert.False(memo.IsReviewerEvidenceAncestor("fixture-root", operand, "949904e4"));
        Assert.False(memo.IsReviewerEvidenceAncestor("fixture-root", "a40e4d77", operand));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void RegisteredAdapter_ClosesWaitUsingRealRepositoryAncestry()
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        var developer = fixture.Oracle("HEAD");
        fixture.Git("commit", "--allow-empty", "-m", "tester");
        var tester = fixture.Oracle("HEAD");
        Assert.NotEqual(developer, tester);
        var scenario = CreateScenario(developerCandidate: developer, testerCandidate: tester, root: fixture.Repo);

        RecordReviewer(scenario, tester);

        AssertClosed(scenario, tester, developerCandidate: true);
    }

    private static void AssertClosed(Scenario scenario, string candidate, bool developerCandidate)
    {
        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.True(scenario.Request.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, scenario.Request.AuthoritativeAnswer!.Origin);
        Assert.Contains($" candidate={candidate} ", scenario.Request.Answer);
        if (developerCandidate)
            Assert.Contains($"developer_candidate={scenario.DeveloperCandidate}", scenario.Request.Answer);
        else
            Assert.DoesNotContain("developer_candidate=", scenario.Request.Answer);
        var received = Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.HumanInputReceived));
        Assert.Equal(scenario.Request.Answer, received.Message);
        Assert.Empty(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.StartsWith(Skipped, StringComparison.Ordinal));
    }

    private static void AssertSkipped(Scenario scenario, string reason)
    {
        Assert.Equal(WorkTaskStatus.Completed, scenario.Reviewer.Status);
        Assert.False(scenario.Request.IsCompleted);
        Assert.Null(scenario.Request.Answer);
        Assert.Same(scenario.Request, Assert.Single(scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id)));
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.HumanInputReceived);
        var skipped = Assert.Single(scenario.Goal.Timeline.Where(item => item.Message.StartsWith(Skipped, StringComparison.Ordinal)));
        Assert.Equal($"{Skipped} request={scenario.Request.Id.Value[..8]} reason={reason}", skipped.Message);
    }

    private static Scenario CreateScenario(string owner = "reviewer", int secondTesterOffset = 2,
        string secondTesterBase = "T", string? testerResult = "T", string developerCandidate = "D",
        string testerCandidate = "T", string root = "C:\\fixture")
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var planner = new TaskSpec(TaskId.New(), "Plan evidence", AgentRole.Planner);
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Verify candidate", AgentRole.Tester);
        var reviewer = new TaskSpec(TaskId.New(), "Inspect diff", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Close evidence after a second Tester round", [planner, developer, tester, reviewer]);
        string[] criteria = ["Full gate passes.", "Focused tests pass.", "Diff inspection is complete."];
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Bind review evidence", criteria,
            VerificationClass.TestVerifiable, [], []) { AcceptanceGateOwnedAcceptanceCriteria = [criteria[0], criteria[1]] });
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var identity = new PlannerEvidenceIdentity(goal.Id.Value, planner.Id.Value, goal.AuthoritativeBrief.Version, 3, "diff-inspection");
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, planner.Id, "Provide diff inspection evidence",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: owner, plannerIdentity: identity,
            plannerEvidence: new PlannerEvidencePayload(3, "diff-inspection", "post-implementation", owner,
                "Inspect diff", "Worker owns this evidence", null, null, null)).Request;
        Assert.False(request.IsCompleted);
        kernel.RecordTaskDispatch(goal.Id, developer.Id, new TaskDispatchRecord(
            "codex-cli", "implement", root, Start, BaseCommit: "A"));
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, developerCandidate);
        kernel.ReportWorkerTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Candidate ready.");
        // Keep both Tester rounds ordered so dispatch history cannot advance the requested second timestamp.
        RecordTester(developerCandidate, testerCandidate, Math.Min(1, secondTesterOffset - 1));
        kernel.RetryTask(goal.Id, tester.Id, "Recheck the committed Tester candidate", RetryCause.ContractClarification,
            invalidateDownstream: false);
        Assert.Null(tester.LastDispatch);
        RecordTester(secondTesterBase == "T" ? testerCandidate : secondTesterBase,
            testerResult == "T" ? testerCandidate : testerResult, secondTesterOffset);
        return new Scenario(kernel, goal, tester, reviewer, request, developerCandidate, root);

        void RecordTester(string baseCommit, string? result, int offset)
        {
            var at = Start.AddSeconds(offset);
            // The second round stores the unchanged head as both base and result; it creates no new commit.
            kernel.RecordTaskDispatch(goal.Id, tester.Id, new TaskDispatchRecord(
                "codex-cli", "verify", root, at, BaseCommit: baseCommit, ResultCommit: result));
            Assert.Equal(at, tester.LastDispatch!.DispatchedAt);
            kernel.RecordTaskVerification(goal.Id, tester.Id, new TaskVerificationRecord(
                "verify", root, 0, "", "", at.AddMilliseconds(1), WorkerResultPresent: true));
            kernel.ReportWorkerTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Completed, "Tester round recorded.");
            Assert.Equal(WorkTaskStatus.Completed, tester.Status);
            Assert.Equal(baseCommit, tester.LastVerification!.ReviewedCommit);
            Assert.Equal(result, tester.LastDispatch!.ResultCommit);
        }
    }

    private static TaskVerificationRecord RecordReviewer(Scenario scenario, string candidate,
        Func<string, string, bool>? ancestry = null, bool pass = true)
    {
        if (!pass)
            scenario.Kernel.MapCriterionEvidenceOwner(scenario.Goal.Id, 0,
                scenario.Goal.AuthoritativeRefinedSpecVersion!.Version, CriterionEvidenceOwner.Acceptance,
                "operator", findingStableId: "gate-proof", expectedCandidateSha: candidate);
        var at = Start.AddSeconds(3);
        scenario.Kernel.RecordTaskDispatch(scenario.Goal.Id, scenario.Reviewer.Id, new TaskDispatchRecord(
            "codex-cli", "review", scenario.Root, at, BaseCommit: candidate));
        var findings = pass ? "[]" :
            """[{"stable_id":"gate-proof","state":"open","location":{"file":"docs/operator-runbook.md","region":"gate-proof"},"description":"Full acceptance evidence remains pending.","severity":"blocking","category":"acceptance-owned"}]""";
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - GateProofTests",
            "commit: none", $"blockers: {(pass ? "none" : "Required owner evidence remains pending.")}",
            $"findings: {findings}", "touched_anchors: []",
            "criteria_verdicts: [" +
                """{"criterion_index":0,"verdict":"not-verifiable","evidence":"Full gate"},""" +
                """{"criterion_index":1,"verdict":"not-verifiable","evidence":"Focused tests"},""" +
                """{"criterion_index":2,"verdict":"met","evidence":"Inspected implementation candidate."}]""",
            $"verdict: {(pass ? "pass" : "needs-work")}",
            "model_fit: fixture/model - adequate - deterministic review", "skills: none", "confidence: high", "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord("review", scenario.Root, 0, output, "", at.AddSeconds(1),
            WorkerResultPresent: true, DispatchStartedAt: at);
        scenario.Kernel.RecordDispatchExecutionResult(scenario.Goal.Id, scenario.Reviewer.Id, verification,
            reviewerEvidenceAncestry: ancestry);
        return scenario.Reviewer.LastVerification!;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Start.AddMinutes(-1);
    }

    private sealed record Scenario(AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Tester,
        TaskSpec Reviewer, HumanInputRequest Request, string DeveloperCandidate, string Root);
}
