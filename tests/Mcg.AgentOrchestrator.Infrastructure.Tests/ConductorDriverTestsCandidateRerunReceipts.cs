using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsCandidateRerunReceipts
{
    [Theory]
    [InlineData("fault")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("apparatus")]
    public void CandidateRerunUsesPositiveExecutionEvidence(string rerunOutcome)
    {
        const string candidateSha = "abc1234";
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
                PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
            FailReviewerNeedsWork(kernel, goal, reviewer, "candidate RED needs a usable baseline",
                findings: [EvidenceFindingWithRequest("Run focused evidence", "rerun-fault",
                    classes: ["ConductorDriverTests"])]);

            var runs = 0;
            var retried = new List<TaskId>();
            string? escalation = null;
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                getLandingFileScopes: _ => [],
                runFocusedEvidence: (_, request) =>
                {
                    runs++;
                    var red = CandidateRedFindingEvidence(
                        request, candidateSha, "OutsideTests.FailingMethod(passed: True)");
                    if (runs == 3)
                    {
                        return rerunOutcome switch
                        {
                            "cancelled" => throw new OperationCanceledException("rerun cancelled"),
                            "fault" => throw new InvalidOperationException("rerun executor failed"),
                            "rejected" => red with { Accepted = false },
                            "apparatus" => red with
                            {
                                Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate)
                                    .Select(arm => arm with
                                    {
                                        Disposition = FindingEvidenceArmDisposition.ApparatusFailure,
                                        Passed = false
                                    }).ToArray(),
                                OutcomeReason = FindingEvidenceOutcomeReason.ApparatusFailure
                            },
                            _ => throw new InvalidOperationException("Unknown rerun outcome.")
                        };
                    }
                    if (runs == 4)
                    {
                        var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                            root, request, candidateSha);
                        return green with
                        {
                            Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                            OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence
                        };
                    }
                    return runs == 1
                        ? red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() }
                        : red with
                        {
                            Arms = red.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Baseline
                                ? arm with { Disposition = FindingEvidenceArmDisposition.ApparatusFailure, Passed = false }
                                : arm).ToArray()
                        };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    retried.Add(taskId);
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
                writeEscalation: (_, _, message) => escalation = message);

            for (var tick = 0; tick < 5 && runs < 3; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, runs);
            Assert.DoesNotContain(developer.Id, retried);
            Assert.DoesNotContain(goal.Timeline, evt =>
                evt.Message.Contains("disposition=candidate-rerun-red", StringComparison.Ordinal));
            if (rerunOutcome == "cancelled")
            {
                Assert.Null(escalation);
                Assert.DoesNotContain(goal.Timeline, evt =>
                    evt.Message.Contains("disposition=candidate-rerun-unusable", StringComparison.Ordinal));
                for (var tick = 0; tick < 4 && retried.Count == 0; tick++)
                    driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
                Assert.Equal(4, runs);
                Assert.Equal([reviewer.Id], retried);
            }
            else
            {
                Assert.Contains("Baseline execution failure", escalation, StringComparison.Ordinal);
                Assert.Single(goal.Timeline.Where(evt =>
                    evt.Message.Contains("disposition=candidate-rerun-unusable", StringComparison.Ordinal)));
                Assert.Empty(retried);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(nameof(ConductorBatchLoopTestsReapFault), true)]
    [InlineData("OtherBatchLoopTestsReapFault", false)]
    public void CandidateRerunRequiresExecutedBaseClassCoverage(string executedClass, bool expectedGreen)
    {
        const string candidateSha = "abc1234";
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
                PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
            FailReviewerNeedsWork(kernel, goal, reviewer, "candidate RED needs a usable baseline",
                findings: [EvidenceFindingWithRequest("Run focused evidence", "rerun-base-coverage",
                    classes: [nameof(ConductorBatchLoopTests)])]);

            var runs = 0;
            var retried = new List<TaskId>();
            string? escalation = null;
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                getLandingFileScopes: _ => [],
                runFocusedEvidence: (_, request) =>
                {
                    runs++;
                    Assert.Equal("Infrastructure.Tests:ConductorBatchLoopTests", request);
                    if (runs == 3)
                    {
                        var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                            root, request, candidateSha, [executedClass]);
                        return green with
                        {
                            Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                            OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence
                        };
                    }
                    var red = CandidateRedFindingEvidence(
                        request, candidateSha, "OutsideTests.FailingMethod(passed: True)");
                    return runs == 1
                        ? red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() }
                        : red with
                        {
                            Arms = red.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Baseline
                                ? arm with { Disposition = FindingEvidenceArmDisposition.ApparatusFailure, Passed = false }
                                : arm).ToArray()
                        };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    retried.Add(taskId);
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
                writeEscalation: (_, _, message) => escalation = message);

            for (var tick = 0; tick < 5 && runs < 3; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(3, runs);
            Assert.DoesNotContain(goal.Timeline, evt =>
                evt.Message.Contains("disposition=candidate-rerun-red", StringComparison.Ordinal));
            if (expectedGreen)
            {
                Assert.Equal([reviewer.Id], retried);
                Assert.Single(goal.Timeline.Where(evt =>
                    evt.Message.Contains("disposition=candidate-rerun-green", StringComparison.Ordinal)));
                Assert.DoesNotContain(goal.Timeline, evt =>
                    evt.Message.Contains("disposition=candidate-rerun-unusable", StringComparison.Ordinal));
                Assert.Null(escalation);
            }
            else
            {
                Assert.Empty(retried);
                Assert.Single(goal.Timeline.Where(evt =>
                    evt.Message.Contains("disposition=candidate-rerun-unusable", StringComparison.Ordinal)));
                Assert.DoesNotContain(goal.Timeline, evt =>
                    evt.Message.Contains("disposition=candidate-rerun-green", StringComparison.Ordinal));
                Assert.Contains("Baseline execution failure", escalation, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Trait("Category", "CrossTick")]
    public void GreenRerunReceiptRunsQueuedRequestBeforeDelivery(bool refuseDispositionWrite, bool refuseCheckpoint)
    {
        const string candidateSha = "abc1234";
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
                PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
            FailReviewerNeedsWork(kernel, goal, reviewer, "both evidence requests need receipts",
                findings:
                [
                    EvidenceFindingWithRequest("Run candidate evidence", "rerun-green",
                        classes: ["ConductorDriverTests"]),
                    EvidenceFindingWithRequest("Run queued evidence", "queued-green",
                        project: "Core.Tests", classes: ["GoalLifecycleTests"])
                ]);

            var requests = new List<string>();
            var retried = new List<TaskId>();
            var refusedWrites = 0;
            var refuseWrite = refuseDispositionWrite;
            void RefuseRerunDispositionWrite()
            {
                if (!refuseWrite || requests.Count != 3 || !Directory.EnumerateFiles(Path.Combine(root, goal.Id.Value), "*.attempt.json")
                    .Select(File.ReadAllText).Any(metadata =>
                        metadata.Contains("candidate-rerun", StringComparison.Ordinal) &&
                        metadata.Contains("\"focusedEvidenceReceiptId\": \"finding-evidence-", StringComparison.Ordinal))) return;
                refusedWrites++;
                throw new IOException("Injected failure writing the GREEN rerun disposition.");
            }

            ConductorDriver CreateDriver() => MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null,
                    conductEventLogWriter: new ConductEventLogWriter(Path.Combine(root, "conduct.jsonl"),
                        beforeRequiredEventDrain: RefuseRerunDispositionWrite)),
                getLandingFileScopes: _ => [],
                runFocusedEvidence: (_, request) =>
                {
                    requests.Add(request);
                    if (requests.Count is 3 or 4)
                    {
                        var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                            root, request, candidateSha);
                        return green with
                        {
                            Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                            OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence
                        };
                    }
                    var red = CandidateRedFindingEvidence(
                        request, candidateSha, "OutsideTests.FailingMethod(passed: True)");
                    return requests.Count == 1
                        ? red with { Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray() }
                        : red with
                        {
                            Arms = red.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Baseline
                                ? arm with { Disposition = FindingEvidenceArmDisposition.ApparatusFailure, Passed = false }
                                : arm).ToArray()
                        };
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                {
                    retried.Add(taskId);
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                },
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
                    kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            var driver = CreateDriver();
            var policy = ConductorAutonomyPolicy.Permissive;
            var observed = FailedGoalFindingObservation.None;
            var rejectCheckpoint = refuseCheckpoint;
            OrchestratorSnapshot? checkpoint = null;
            void Checkpoint(GoalId goalId)
            {
                Assert.Equal(goal.Id, goalId);
                Assert.Single(driver.FocusedEvidenceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value])
                    .Where(attempt => attempt.FocusedEvidenceBatchId?.EndsWith("-candidate-rerun", StringComparison.Ordinal) == true));
                if (rejectCheckpoint)
                    throw new IOException("Injected GREEN rerun checkpoint failure.");
                checkpoint = kernel.ExportSnapshot();
            }
            driver.CheckpointFindingEvidenceCompletion = Checkpoint;

            void ObserveRerun()
            {
                for (var tick = 0; tick < 5 && requests.Count < 3; tick++)
                    Assert.True(driver.TryBuildFindingEvidenceRequest(goal, reviewer, policy, out observed));
            }
            if (refuseCheckpoint)
                Assert.Equal("Injected GREEN rerun checkpoint failure.", Assert.Throws<IOException>(ObserveRerun).Message);
            else
                ObserveRerun();

            Assert.Equal(3, requests.Count);
            Assert.Empty(retried);
            if (refuseDispositionWrite || refuseCheckpoint)
            {
                if (refuseDispositionWrite)
                {
                    Assert.True(refusedWrites > 0);
                    Assert.Equal(FailedGoalFindingObservationKind.FindingEvidenceReceiptPersistenceFailed, observed.Kind);
                    Assert.Null(observed.BatchClosure);
                }
                Assert.Null(checkpoint);
                Assert.DoesNotContain(reviewer.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []),
                    receipt => receipt.Passed);
                Assert.DoesNotContain(goal.Timeline, item =>
                    item.Message.Contains("disposition=candidate-rerun-green;", StringComparison.Ordinal));
                Assert.Single(driver.FocusedEvidenceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value])
                    .Where(attempt => attempt.FocusedEvidenceBatchId?.EndsWith("-candidate-rerun", StringComparison.Ordinal) == true));
                kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
                goal = kernel.GetGoal(goal.Id);
                reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
                refuseWrite = false;
                rejectCheckpoint = false;
                driver = CreateDriver();
                driver.CheckpointFindingEvidenceCompletion = Checkpoint;
                Assert.True(driver.TryBuildFindingEvidenceRequest(goal, reviewer, policy, out observed));
                Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, observed.Kind);
                Assert.Equal(3, requests.Count);
            }

            kernel = AgentOrchestratorKernel.FromSnapshot(Assert.IsType<OrchestratorSnapshot>(checkpoint));
            goal = kernel.GetGoal(goal.Id);
            reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            driver = CreateDriver();
            var first = reviewer.VerificationHistory.Last().MergedReviewFindings!
                .Single(finding => finding.StableId == "rerun-green");
            var firstReceipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!
                .Where(receipt => receipt.ReceiptId == first.EvidenceOutcome?.ReceiptId));
            Assert.False(string.IsNullOrWhiteSpace(firstReceipt.ExecutionBasisIdentity));
            Assert.Contains(firstReceipt.RequestDispositions!, disposition =>
                disposition.FindingStableId == "rerun-green" &&
                disposition.Disposition.StartsWith("executed-", StringComparison.Ordinal));
            var rerunEvent = Assert.Single(goal.Timeline.Where(evt =>
                evt.Message.Contains("disposition=candidate-rerun-green", StringComparison.Ordinal)));
            Assert.Contains($"receipt_id={firstReceipt.ReceiptId}", rerunEvent.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(driver.FocusedEvidenceAttemptCoordinator.GetUnreconciledAttempts([goal.Id.Value]),
                attempt => attempt.FocusedEvidenceBatchId?.EndsWith("-candidate-rerun", StringComparison.Ordinal) == true);

            var identity = Assert.IsType<FindingEvidenceBatchIdentity>(FindingRoundBatchCoordinator.Identity(driver, goal, reviewer));
            var pending = FindingRoundBatchAccounting.Summarize(goal, reviewer, identity, "pending");
            Assert.Equal(1, pending.ReviewerCallsAvoided);
            Assert.Equal(0, pending.TesterCallsAvoided);
            var witness = Assert.Single(pending.EligibilityWitnesses, item => item.ReceiptId == firstReceipt.ReceiptId);
            Assert.Equal(AgentRole.Reviewer, witness.Role);
            Assert.Empty(retried);

            for (var tick = 0; tick < 4 && retried.Count == 0; tick++)
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(4, requests.Count);
            Assert.Contains(requests, request => request.Contains("Core.Tests:GoalLifecycleTests", StringComparison.Ordinal));
            Assert.Equal([reviewer.Id], retried);
            Assert.DoesNotContain(developer.Id, retried);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
