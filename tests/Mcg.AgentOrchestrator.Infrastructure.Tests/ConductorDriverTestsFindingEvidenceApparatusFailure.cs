using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFindingEvidenceApparatusFailure
{
    private const int CrossTickHangFailsafeMilliseconds = 30_000;

    [Xunit.Fact]
    public async Task MixedProjectExecution_ReloadKeepsZeroTestMembersPending()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        FindingEvidenceSelection[] selections = [
            new("Core.Tests", "DispatchExecutionTests"),
            new("Infrastructure.Tests", "GoalAcceptanceVerifierTests")];
        var positive = EvidenceFindingWithRequest("Core execution required.", id: "positive",
            project: selections[0].TestProject, classes: [selections[0].TestClass]);
        var empty = EvidenceFindingWithRequest("Infrastructure execution required.", id: "empty",
            classes: [selections[1].TestClass]);
        var union = EvidenceFindingWithRequest("Both projects must execute.", id: "union") with
        {
            EvidenceRequest = new FindingEvidenceRequest(selections)
        };
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [positive, empty, union]);
        AcceptanceManifestCheck[] planned = [new() { Name = "Core" }, new() { Name = "Infrastructure" }];
        var coverage = new FocusedEvidenceCoverage(selections.Select((selection, index) =>
            new FocusedEvidenceTargetCoverage($"{selection.TestProject}:{selection.TestClass}", [planned[index].Name])).ToArray());
        AcceptanceCheckResult[] results = [
            new(planned[0].Name, true, 0, "passed", ExecutedTestCount: 1),
            new(planned[1].Name, false, 8, "0 tests selected", ExecutedTestCount: 0,
                FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure)];
        var checkRuns = 0;
        // Drive the real check-to-selection mapping; supplying NotRunSelections by hand
        // would hide the mixed positive/zero execution defect this regression targets.
        var candidate = await FocusedEvidenceExecution.RunFocusedEvidenceArmAsync(
            _ => throw new InvalidOperationException("No scoped runner is needed."),
            (_, checks, _, _, _, _, _, continueAfterFailure, _) =>
            {
                Assert.True(continueAfterFailure);
                Assert.Equal(planned, checks);
                checkRuns++;
                return Task.FromResult<IReadOnlyList<AcceptanceCheckResult>>(results);
            },
            _ => "", FindingEvidenceArm.Candidate, candidateSha, "unused", null, planned, coverage,
            null, null, null, CancellationToken.None, continueAfterFailure: true, armContextApplied: true);
        var focusedRuns = 0;
        var retriedTaskIds = new List<TaskId>();
        var dispatchStarts = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            dispatchAndStart: _ =>
            {
                dispatchStarts++;
                return DispatchStartOutcome.Started();
            },
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Equal(2, request.Split(';').Length);
                return new FocusedEvidenceRunResult(request, true, false, candidate.Summary, results, coverage,
                    Arms: [candidate, candidate with
                    {
                        Arm = FindingEvidenceArm.Baseline, Sha = "baseline-sha",
                        Disposition = FindingEvidenceArmDisposition.Green, Passed = true,
                        Checks = results.Select(check => check with
                        {
                            Passed = true, ExitCode = 0, ExecutedTestCount = 1, FailureClassification = null
                        }).ToArray(), NotRunSelections = []
                    }], OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, kind) =>
            {
                retriedTaskIds.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: kind);
            },
            recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
                kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);

        Assert.Equal(1, checkRuns);
        Assert.Equal(1, focusedRuns);
        Assert.Empty(retriedTaskIds);
        Assert.Equal(0, dispatchStarts);
        var reloaded = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
        var durable = reloaded.Goals.Single().Tasks.Single(task => task.Id == reviewer.Id);
        var verification = durable.VerificationHistory.Last();
        var receipt = Assert.Single(verification.FindingEvidenceReceipts!);
        var candidateReceipt = Assert.Single(receipt.Arms!, arm => arm.Arm == FindingEvidenceArm.Candidate);
        Assert.Equal(1, candidateReceipt.ExecutedTestCount);
        Assert.Equal(selections[1], Assert.Single(candidateReceipt.NotRunSelections));
        var dispositions = verification.FindingEvidenceRequestDispositions!;
        Assert.Equal(3, dispositions.Count);
        Assert.Equal(3, receipt.RequestDispositions!.Count);
        Assert.Equal(FindingEvidenceRequestDecision.CoveredByReceipt,
            dispositions.Single(item => item.FindingStableId == "positive").Decision);
        foreach (var stableId in new[] { "empty", "union" })
        {
            var disposition = dispositions.Single(item => item.FindingStableId == stableId);
            Assert.Equal(FindingEvidenceRequestDecision.PendingExecution, disposition.Decision);
            Assert.Equal("incomplete", disposition.Disposition);
            Assert.Equal("candidate-selections-not-executed", disposition.Reason);
            Assert.Equal(candidateSha, disposition.CandidateSha);
            Assert.Equal(receipt.FindingRoundFingerprint, disposition.FindingRoundFingerprint);
            Assert.Null(disposition.CoveringRequestIdentity);
            Assert.Equal(receipt.ReceiptId, Assert.Single(disposition.SourceReceiptIds!));
            var receiptDisposition = receipt.RequestDispositions.Single(item => item.FindingStableId == stableId);
            Assert.Equal(disposition.Decision, receiptDisposition.Decision);
            Assert.Equal(disposition.Disposition, receiptDisposition.Disposition);
            Assert.Equal(disposition.Reason, receiptDisposition.Reason);
            Assert.Equal(disposition.CandidateSha, receiptDisposition.CandidateSha);
            Assert.Equal(disposition.FindingRoundFingerprint, receiptDisposition.FindingRoundFingerprint);
            Assert.Equal(disposition.CoveringRequestIdentity, receiptDisposition.CoveringRequestIdentity);
            Assert.Equal(disposition.SourceReceiptIds, receiptDisposition.SourceReceiptIds);
        }
        Assert.Equal(3, verification.MergedReviewFindings!.Count);
        Assert.All(verification.MergedReviewFindings, finding =>
            Assert.Equal(finding.StableId == "positive" ? FindingEvidenceExecutionState.ExecutedOnCandidate
                    : FindingEvidenceExecutionState.PendingExecution,
                FindingEvidenceExecutionClassifier.Classify(durable, finding, candidateSha)));
    }

    [Xunit.Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(null)]
    [Xunit.Trait("Category", "CrossTick")]
    public void FocusedSelectionApparatusFailure_PreservesReceiptAndBoundsRecovery(int? executedTestCount)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The selection must execute at least one test.",
            id: "zero-selection",
            classes: ["GoalAcceptanceVerifierTests", "ConductorDriverTests"]);
        var coveredFinding = EvidenceFindingWithRequest(
            "The narrower selection must also execute.",
            id: "covered-zero-selection",
            classes: ["GoalAcceptanceVerifierTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding, coveredFinding]);
        var retriedTaskIds = new List<TaskId>();
        var escalations = new List<string>();
        var dispatchStarts = 0;
        var check = new AcceptanceCheckResult(
            "reviewer focused evidence",
            Passed: false,
            ExitCode: 8,
            OutputTail: "0 tests were selected",
            TestResultPaths: ["C:\\receipts\\zero.trx"],
            FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
            ExecutedTestCount: executedTestCount);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            dispatchAndStart: _ =>
            {
                dispatchStarts++;
                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, reason) => escalations.Add(reason),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "focused selection apparatus failure",
                Checks: [check],
                Arms:
                [
                    new FocusedEvidenceArmRunResult(
                        FindingEvidenceArm.Candidate,
                        "abc1234",
                        FindingEvidenceArmDisposition.ApparatusFailure,
                        Accepted: true,
                        Passed: false,
                        "executed=0",
                        [check]),
                    // Baseline execution cannot establish coverage on the candidate.
                    new FocusedEvidenceArmRunResult(
                        FindingEvidenceArm.Baseline,
                        "baseline-sha",
                        FindingEvidenceArmDisposition.Green,
                        Accepted: true,
                        Passed: true,
                        "executed=1",
                        [check with { Passed = true, ExitCode = 0, ExecutedTestCount = 1,
                            FailureClassification = null }])
                ],
                OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskIds.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
                kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        var policy = ConductorAutonomyPolicy.Permissive;
        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, policy).Outcome);

        Assert.Empty(retriedTaskIds);
        Assert.Empty(escalations);
        Assert.Equal(0, dispatchStarts);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        var reloaded = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
        var durable = reloaded.Goals.Single().Tasks.Single(task => task.Id == reviewer.Id);
        var verification = durable.VerificationHistory.Last();
        var receipt = Assert.Single(verification.FindingEvidenceReceipts!);
        var candidate = Assert.Single(receipt.Arms!, arm => arm.Arm == FindingEvidenceArm.Candidate);
        Assert.Equal(FindingEvidenceArmDisposition.ApparatusFailure, candidate.Disposition);
        Assert.Equal(executedTestCount, candidate.ExecutedTestCount);
        Assert.Empty(candidate.NotRunSelections);
        Assert.Contains("zero.trx", candidate.ReceiptPaths!.Single(), StringComparison.Ordinal);
        Assert.Equal(2, verification.FindingEvidenceRequestDispositions!.Count);
        Assert.Equal(2, receipt.RequestDispositions!.Count);
        Assert.All(verification.MergedReviewFindings!, recordedFinding =>
        {
            var outcome = recordedFinding.EvidenceOutcome;
            Assert.False(outcome?.Honoured);
            Assert.Equal(FindingEvidenceNotHonouredReason.SelectionApparatusFailure, outcome?.Reason);
            Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, outcome?.ResultReason);
            Assert.Equal(receipt.ReceiptId, outcome?.ReceiptId);
            Assert.Equal(FindingEvidenceExecutionState.PendingExecution,
                FindingEvidenceExecutionClassifier.Classify(durable, recordedFinding, "abc1234"));
        });
        Assert.All(verification.FindingEvidenceRequestDispositions, disposition =>
        {
            Assert.Equal(FindingEvidenceRequestDecision.PendingExecution, disposition.Decision);
            Assert.Equal("incomplete", disposition.Disposition);
            Assert.Equal("candidate-selections-not-executed", disposition.Reason);
            Assert.Equal("abc1234", disposition.CandidateSha);
            Assert.Equal(receipt.FindingRoundFingerprint, disposition.FindingRoundFingerprint);
            Assert.Null(disposition.CoveringRequestIdentity);
            Assert.Equal(receipt.ReceiptId, Assert.Single(disposition.SourceReceiptIds!));
            var receiptDisposition = receipt.RequestDispositions.Single(item =>
                item.FindingStableId == disposition.FindingStableId);
            Assert.Equal(disposition.Decision, receiptDisposition.Decision);
            Assert.Equal(disposition.Disposition, receiptDisposition.Disposition);
            Assert.Equal(disposition.Reason, receiptDisposition.Reason);
            Assert.Equal(disposition.CandidateSha, receiptDisposition.CandidateSha);
            Assert.Equal(disposition.SourceReceiptIds, receiptDisposition.SourceReceiptIds);
        });

        kernel = reloaded;
        goal = kernel.GetGoal(goal.Id);
        var runCap = FindingRoundBatchSchedule.MaximumCompletedRuns;
        int ChargedRuns() => goal.Timeline.Count(item => item.TaskId == reviewer.Id &&
            item.Kind == ProgressKind.FindingEvidenceRunRecorded);

        Assert.Equal(1, ChargedRuns());
        for (var run = 2; run < runCap; run++)
        {
            Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, policy).Outcome);
            Assert.Equal(run, ChargedRuns());
            Assert.Empty(retriedTaskIds);
            Assert.Empty(escalations);
            Assert.Equal(0, dispatchStarts);
        }

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(driver.AdvanceOnce(goal, policy).Outcome);
        Assert.Equal(runCap, ChargedRuns());
        Assert.Contains("unavailable required evidence", Assert.Single(escalations), StringComparison.Ordinal);

        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, policy).Outcome);
        Assert.Equal(runCap, ChargedRuns());
        Assert.Single(escalations);
        Assert.Empty(retriedTaskIds);
        Assert.Equal(0, dispatchStarts);
    }
}
