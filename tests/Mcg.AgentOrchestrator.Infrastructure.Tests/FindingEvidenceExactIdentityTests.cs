using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;
using static ConductorDriverTestsFindingEvidenceReuse;

public sealed class FindingEvidenceExactIdentityTests
{
    private const string OriginalCandidate = "abc1234abcdef1234abcdef1234abcdef1234abcd12";
    private const string ChangedCandidate = "def5678abcdef1234abcdef1234abcdef1234abcd12";
    private const string SimpleClass = "ConductorDriverTests";
    private const string FabricatedClass = "Invented.Namespace.ConductorDriverTests";
    private const string SimpleSelection = "Infrastructure.Tests:" + SimpleClass;
    private const string FabricatedSelection = "Infrastructure.Tests:" + FabricatedClass;

    [Xunit.Fact]
    public void FabricatedNamespaceQualifiedSelectionRunsOnceWhileSimpleNameReusesGreen()
    {
        using var scenario = new Scenario();
        var source = EvidenceFindingWithRequest("Establish simple-name coverage.", id: "simple-source",
            classes: [SimpleClass]);
        scenario.Report(source);
        scenario.AdvanceUntil(() => scenario.Tester.Status == WorkTaskStatus.Completed);
        Assert.Equal([SimpleSelection], scenario.FocusedRequests);
        var green = scenario.ReceiptFor(source);

        var fabricated = EvidenceFindingWithRequest("Request a fabricated qualified identity.", id: "fabricated",
            classes: [FabricatedClass]);
        scenario.Report(fabricated, source with { State = ReviewFindingState.Resolved });
        scenario.AdvanceUntil(() => scenario.RecordedFinding(fabricated).EvidenceOutcome is not null);

        Assert.Equal([SimpleSelection, FabricatedSelection], scenario.FocusedRequests);
        var recorded = scenario.RecordedFinding(fabricated);
        var zeroMatch = scenario.ReceiptFor(fabricated);
        Assert.NotEqual(green.ReceiptId, zeroMatch.ReceiptId);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, recorded.EvidenceOutcome!.ResultReason);
        Assert.Equal(ReviewFindingState.Open, recorded.State);
        Assert.Equal(FindingEvidenceExecutionState.PendingExecution,
            FindingEvidenceExecutionClassifier.Classify(scenario.Tester, recorded, OriginalCandidate));

        var simple = EvidenceFindingWithRequest("Request the proven simple identity.", id: "simple-repeat",
            classes: [SimpleClass]);
        scenario.Report(simple, fabricated with { State = ReviewFindingState.Resolved });
        scenario.AdvanceUntil(() => scenario.Tester.Status == WorkTaskStatus.Completed);

        Assert.Equal([SimpleSelection, FabricatedSelection], scenario.FocusedRequests);
        AssertReused(scenario.RecordedFinding(simple), green, "reused-covered-green:exact");
    }

    [Xunit.Fact]
    public void LaterZeroMatchApparatusReceiptDoesNotEraseProvenGreenCoverage()
    {
        using var scenario = new Scenario();
        var source = EvidenceFindingWithRequest("Establish coverage for two classes.", id: "green-source",
            classes: ["GoalAcceptanceVerifierTests", SimpleClass]);
        scenario.Report(source);
        scenario.AdvanceUntil(() => scenario.Tester.Status == WorkTaskStatus.Completed);
        var green = scenario.ReceiptFor(source);
        Assert.Single(scenario.FocusedRequests);

        var fabricated = EvidenceFindingWithRequest("Record a later zero-match run.", id: "later-zero-match",
            classes: [FabricatedClass]);
        scenario.Report(fabricated, source with { State = ReviewFindingState.Resolved });
        scenario.AdvanceUntil(() => scenario.RecordedFinding(fabricated).EvidenceOutcome is not null);
        Assert.Equal(2, scenario.FocusedRequests.Count);
        Assert.Equal(FabricatedSelection, scenario.FocusedRequests[1]);
        var zeroMatch = scenario.ReceiptFor(fabricated);
        Assert.Equal(green.CandidateSha, zeroMatch.CandidateSha);
        Assert.Equal(green.ExecutionBasisIdentity, zeroMatch.ExecutionBasisIdentity);
        Assert.NotEqual(green.FindingRoundFingerprint, zeroMatch.FindingRoundFingerprint);
        var candidate = Assert.Single(zeroMatch.Arms!, arm => arm.Arm == FindingEvidenceArm.Candidate);
        Assert.Equal(FindingEvidenceArmDisposition.ApparatusFailure, candidate.Disposition);
        Assert.Equal(0, candidate.ExecutedTestCount);
        Assert.Empty(candidate.NotRunSelections);

        var covered = EvidenceFindingWithRequest("Request a covered subset after zero matches.", id: "covered-subset",
            classes: [SimpleClass]);
        // Reuse would normally remove the covered selection before a fresh run. Record an
        // overlapping receipt in this round to exercise authority with the actual zero-match arms.
        var overlapping = zeroMatch with { ReceiptId = "overlapping-zero-match", Request = covered.EvidenceRequest! };
        var zeroOutcome = scenario.RecordedFinding(fabricated).EvidenceOutcome!;
        scenario.Kernel.RecordFindingEvidenceOutcome(scenario.Goal.Id, scenario.Tester.Id, fabricated.StableId,
            zeroOutcome with { ReceiptId = overlapping.ReceiptId }, overlapping);

        scenario.Report(covered, fabricated with { State = ReviewFindingState.Resolved });
        var recordedRuns = scenario.Goal.Timeline.Count(item => item.Kind == ProgressKind.FindingEvidenceRunRecorded);
        var recordedRetries = scenario.Goal.Timeline.Count(item => item.Kind == ProgressKind.TaskRetried);
        var dispatchAttempts = scenario.DispatchAttempts;
        scenario.AdvanceUntil(() => scenario.Tester.Status == WorkTaskStatus.Completed);

        Assert.Equal(2, scenario.FocusedRequests.Count);
        Assert.Equal(recordedRuns, scenario.Goal.Timeline.Count(item => item.Kind == ProgressKind.FindingEvidenceRunRecorded));
        Assert.Equal(recordedRetries, scenario.Goal.Timeline.Count(item => item.Kind == ProgressKind.TaskRetried));
        Assert.Equal(dispatchAttempts, scenario.DispatchAttempts);
        AssertReused(scenario.RecordedFinding(covered), green, "reused-covered-green:subset");

        var missing = EvidenceFindingWithRequest("Request a class absent from retained executions.",
            id: "missing-class", classes: ["WorkerProcessJobsTests"]);
        scenario.Report(missing, covered with { State = ReviewFindingState.Resolved });
        scenario.AdvanceUntil(() => scenario.Tester.Status == WorkTaskStatus.Completed);
        Assert.Equal(3, scenario.FocusedRequests.Count);
        Assert.Equal("Infrastructure.Tests:WorkerProcessJobsTests", scenario.FocusedRequests[2]);
        Assert.Equal(OriginalCandidate, scenario.ReceiptFor(missing).CandidateSha);

        scenario.CandidateSha = ChangedCandidate;
        var changed = EvidenceFindingWithRequest("Request the covered class at a changed candidate.", id: "changed",
            classes: [SimpleClass]);
        scenario.Report(changed, missing with { State = ReviewFindingState.Resolved });
        scenario.AdvanceUntil(() => scenario.Tester.Status == WorkTaskStatus.Completed);
        Assert.Equal(4, scenario.FocusedRequests.Count);
        Assert.Equal(SimpleSelection, scenario.FocusedRequests[3]);
        Assert.Equal(ChangedCandidate, scenario.ReceiptFor(changed).CandidateSha);
    }

    private static void AssertReused(ReviewFinding finding, FindingEvidenceReceipt green, string reason)
    {
        Assert.Equal(ReviewFindingState.Resolved, finding.State);
        var outcome = finding.EvidenceOutcome!;
        Assert.True(outcome.Honoured);
        Assert.Equal(green.ReceiptId, outcome.ReceiptId);
        Assert.Equal([green.ReceiptId], outcome.SourceReceiptIds);
        Assert.Equal(SimpleSelection, outcome.RequestedSelectionIdentity);
        Assert.Equal(reason, outcome.DecisionReason);
    }

    private static FocusedEvidenceRunResult ZeroMatch(string request, string candidateSha)
    {
        var check = new AcceptanceCheckResult("focused evidence", Passed: false, ExitCode: 8,
            OutputTail: "0 tests were selected", TestResultPaths: ["C:\\receipts\\zero.trx"],
            FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
            ExecutedTestCount: 0);
        return new FocusedEvidenceRunResult(request, Accepted: true, Passed: false,
            Summary: "focused selection apparatus failure", Checks: [check],
            Arms:
            [
                new(FindingEvidenceArm.Candidate, candidateSha, FindingEvidenceArmDisposition.ApparatusFailure,
                    Accepted: true, Passed: false, "executed=0", [check]),
                new(FindingEvidenceArm.Baseline, "baseline-sha", FindingEvidenceArmDisposition.Green,
                    Accepted: true, Passed: true, "executed=1",
                    [check with { Passed = true, ExitCode = 0, ExecutedTestCount = 1, FailureClassification = null }])
            ],
            OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure);
    }

    private sealed class Scenario : IDisposable
    {
        private readonly string _artifactRoot = InfrastructureTestSupport.CreateTempDirectory();
        private readonly ConductorDriver _driver;
        private int _tick;

        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Tester { get; }
        public string CandidateSha { get; set; } = OriginalCandidate;
        public List<string> FocusedRequests { get; } = [];
        public int DispatchAttempts { get; private set; }

        public Scenario()
        {
            (Kernel, Goal) = SoftwareGoal();
            Tester = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Tester.Id))
            {
                PassVerification(Kernel, Goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
                if (task.RequiredRole == AgentRole.Developer)
                    Kernel.RecordDispatchResultCommit(Goal.Id, task.Id, OriginalCandidate);
            }

            _driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    _artifactRoot, TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (_, request) =>
                {
                    FocusedRequests.Add(request);
                    if (request == FabricatedSelection)
                        return ZeroMatch(request, CandidateSha);

                    var green = RetainedEvidenceWithExecutedClasses(_artifactRoot, request, CandidateSha);
                    return green with { Arms = [green.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate)] };
                },
                dispatchAndStart: _ =>
                {
                    DispatchAttempts++;
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                    Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) =>
                    Kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
                    Kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    Kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));
        }

        public void Report(params ReviewFinding[] findings)
        {
            if (Tester.LastVerification is not null)
                Kernel.RetryTask(Goal.Id, Tester.Id, "Set up the next Tester report.");

            DispatchTask(Kernel, Goal, Tester, "test", baseCommit: CandidateSha);
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: none", "commands: inspect focused behavior",
                "tests: deferred - acceptance owns execution", "commit: none", "blockers: none",
                $"findings: {JsonSerializer.Serialize(findings)}", "touched_anchors: []", "verdict: needs-work",
                "model_fit: fixture/model - adequate - source verification", "skills: none", "confidence: high",
                "END_WORKER_RESULT");
            Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id, new TaskVerificationRecord(
                "test", "C:\\tmp", 0, output, "", DateTimeOffset.UtcNow,
                StandardOutputPath: "C:\\tmp\\worker.out.log", WorkerResultPresent: true));
        }

        public ReviewFinding RecordedFinding(ReviewFinding finding) =>
            Tester.VerificationHistory.Last().MergedReviewFindings!.Single(item => item.StableId == finding.StableId);

        public FindingEvidenceReceipt ReceiptFor(ReviewFinding finding) =>
            Assert.Single(Tester.VerificationHistory.Last().FindingEvidenceReceipts!,
                receipt => receipt.ReceiptId == RecordedFinding(finding).EvidenceOutcome!.ReceiptId);

        public void AdvanceUntil(Func<bool> observed)
        {
            for (var step = 0; step < 6 && !observed(); step++)
            {
                _driver.BeginTick(Kernel, ++_tick);
                _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
            }

            Assert.True(observed(), "The Tester evidence outcome or receipt closure was not recorded.");
        }

        public void Dispose() => Directory.Delete(_artifactRoot, recursive: true);
    }
}
