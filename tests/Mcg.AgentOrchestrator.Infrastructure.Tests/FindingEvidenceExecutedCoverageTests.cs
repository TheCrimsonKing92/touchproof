using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: each scenario owns its in-memory kernel and temporary receipt directory.
public sealed class FindingEvidenceExecutedCoverageTests
{
    private static readonly string[] Classes =
    [
        "AdvanceLoopTests", "AutoReviewRetryConvergenceBriefBuilderTests", "ChaosGateAssignedScopeIncompleteTests",
        "ChaosGateDispatchDirtyWorktreeTests", "ChaosGateDispatchNoChangeTests", "CitedPriorEvidenceResolverTests",
        "CliCommandTestsSubscriptionDispatchCommands", "ConductorBatchLoopTestsReapingDetach", "ConductorBatchLoopTestsWatchProgress",
        "ConductorBatchLoopVerificationReconcileTests", "ConductorDriverTests", "ConductorDriverTestsDispatchRecovery",
        "DirtyDispatchRecoveryViewTests", "DispatchRecoveryPolicyTests", "FailureTriageDecisionTests", "GoalRefinementTests",
        "InterruptedWorkCheckpointContinuationTests", "LandingExecutorTests", "PlannerEvidenceDispatchTests",
        "PlannerOutputContractTests", "PlannerSamplingDispatchTests", "PreDispatchIntegrationNoChangeTests"
    ];

    [Xunit.Fact]
    public void CoverageSubtractsOnlyCandidateNotRunSelectionsAndPreservesOrder()
    {
        var selections = Classes.Select(Selection).ToArray();
        var arm = new FindingEvidenceArmReceipt(FindingEvidenceArm.Candidate, "candidate",
            FindingEvidenceArmDisposition.Inconclusive, true, false, "partial", NotRunSelections: selections[17..]);
        var receipt = new FindingEvidenceReceipt("receipt", "candidate", new(selections), true, false, "partial",
            Arms: [arm, arm with { Arm = FindingEvidenceArm.Baseline, NotRunSelections = selections[..17] }]);
        Assert.Equal(selections[..17], FindingEvidenceExecutedCoverage.ExecutedSelections(receipt, Format));
    }

    [Xunit.Fact]
    public void CoverageExpandsUnionsWithoutReorderingRequestedSelections()
    {
        FindingEvidenceSelection[] selections = [Selection("GammaTests"), Selection("AlphaTests|BetaTests"), Selection("DeltaTests")];
        var arm = new FindingEvidenceArmReceipt(FindingEvidenceArm.Candidate, "candidate",
            FindingEvidenceArmDisposition.Inconclusive, true, false, "partial", NotRunSelections: [Selection("AlphaTests")]);
        var receipt = new FindingEvidenceReceipt("receipt", "candidate", new(selections), true, false, "partial", Arms: [arm]);

        Assert.Equal([Selection("GammaTests"), Selection("BetaTests"), Selection("DeltaTests")],
            FindingEvidenceExecutedCoverage.ExecutedSelections(receipt, Format));
    }

    [Xunit.Fact]
    public void LegacyArmWithoutNotRunFieldDeserializesToEmptyCoverageExclusions()
    {
        var arm = JsonSerializer.Deserialize<FindingEvidenceArmReceipt>("""
            {"Arm":"Candidate","Sha":"candidate","Disposition":"Green","Accepted":true,"Passed":true,"Summary":"legacy"}
            """)!;
        Assert.Empty(arm.NotRunSelections);
        var selections = Classes.Select(Selection).ToArray();
        var receipt = new FindingEvidenceReceipt("legacy", "candidate", new(selections), true, true, "legacy", Arms: [arm]);
        Assert.Equal(selections, FindingEvidenceExecutedCoverage.ExecutedSelections(receipt, Format));
    }

    [Xunit.Fact]
    public void PreTesterCoverageExcludesWholeKeysAndClassNames()
    {
        var covered = FindingEvidenceExecutedCoverage.ExecutedSelections(
            ["Infrastructure.Tests:AlphaTests", "Core.Tests:BetaTests", "Infrastructure.Tests:GammaTests"],
            ["alphatests", "core.tests:betatests"]);
        Assert.Equal(["Infrastructure.Tests:GammaTests"], covered);
    }

    [Xunit.Fact]
    public void RequestForFiveNotRunAndOneFailedSelectionExecutesExactlySix()
    {
        Assert.Equal(22, Classes.Length);
        var requested = Classes[17..].Append(Classes[0]).ToArray();
        VerifyReuse(recordNotRun: true, firstRed: true, requested, expectedNew: requested);
    }

    [Xunit.Fact]
    public void GreenReceiptCannotCoverRecordedNotRunSelections()
    {
        // Deliberately inconsistent old-style green evidence discriminates requested coverage
        // from executed coverage, even when its retained TRX claims all requested classes.
        var requested = Classes[17..].Append(Classes[0]).ToArray();
        VerifyReuse(recordNotRun: true, firstRed: false, requested, expectedNew: Classes[17..]);
    }

    [Xunit.Fact]
    public void RepeatedPartialGreen_UsesOriginalRunAllowanceWithoutReviewerRetry()
    {
        VerifyReuse(recordNotRun: true, firstRed: false, Classes[17..], expectedNew: Classes[17..],
            exhaustRemainder: true);
    }

    [Xunit.Fact]
    public void FullyExecutedGreenReceiptReusesSubsetWithoutExecution()
    {
        VerifyReuse(recordNotRun: false, firstRed: false, Classes[17..], expectedNew: []);
    }

    private static void VerifyReuse(bool recordNotRun, bool firstRed, string[] requested, string[] expectedNew,
        bool exhaustRemainder = false)
    {
        const string candidateSha = "abc1234";
        var artifactRoot = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var requester = goal.Tasks.Single(task => task.RequiredRole == (firstRed ? AgentRole.Tester : AgentRole.Reviewer));
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            if (firstRed)
                PassVerification(kernel, goal, developer, hasCommittedChanges: true);
            else
                foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
                    PassVerification(kernel, goal, task);
            var executions = new List<string>();
            var dispatchCalls = 0;
            var escalations = new List<string>();
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                runFocusedEvidence: (_, request) =>
                {
                    executions.Add(request);
                    var evidence = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(
                        artifactRoot, request, candidateSha);
                    if (executions.Count != 1 && !exhaustRemainder) return evidence;
                    var candidate = evidence.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
                    candidate = candidate with { NotRunSelections = recordNotRun ? Classes[17..].Select(Selection).ToArray() : [] };
                    if (firstRed)
                    {
                        var failed = candidate.Checks[0] with { Passed = false, ExitCode = 2, FailingTestIdentities = [Classes[0] + ".TimedOut"] };
                        candidate = candidate with { Disposition = FindingEvidenceArmDisposition.Red, Passed = false, Checks = [failed] };
                        evidence = evidence with { Passed = false, Checks = [failed] };
                    }
                    return evidence with { Arms = evidence.Arms.Select(arm => arm.Arm == FindingEvidenceArm.Candidate ? candidate : arm).ToArray() };
                },
                dispatchAndStart: _ =>
                {
                    dispatchCalls++;
                    return DispatchStartOutcome.Started();
                },
                writeEscalationWithResult: (_, _, reason) =>
                {
                    escalations.Add(reason);
                    return new(0, "ok", 0, "ok");
                },
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                    kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
                recordFindingEvidenceRequest: (goalId, taskId, message) => kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) => kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));
            var initial = EvidenceFindingWithRequest("Initial wide selection.", id: "wide", category: FindingCategory.TestEvidence, classes: Classes);
            if (firstRed) RecordTesterRequest(kernel, goal, requester, initial);
            else FailReviewerNeedsWork(kernel, goal, requester, "initial", findings: [initial]);
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            Assert.Single(executions);
            var sourceVerification = requester.VerificationHistory.Last();
            var sourceReceipt = Assert.Single(sourceVerification.FindingEvidenceReceipts!);
            Assert.Equal(recordNotRun ? 5 : 0, sourceReceipt.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate).NotRunSelections.Count);

            if (recordNotRun && !firstRed)
            {
                IEnumerable<ProgressEvent> Closures() => goal.Timeline.Where(item => item.TaskId == requester.Id &&
                    item.Message.StartsWith("finding-evidence batch-closed;", StringComparison.Ordinal));
                IEnumerable<ProgressEvent> Retries() => goal.Timeline.Where(item => item.TaskId == requester.Id &&
                    item.Kind == ProgressKind.TaskRetried);
                FindingRoundBatchAccounting.Summary ClosingSummary()
                {
                    var message = Assert.Single(Closures()).Message;
                    return JsonSerializer.Deserialize<FindingRoundBatchAccounting.Summary>(message[message.IndexOf('{')..])!;
                }

                Assert.Null(sourceVerification.CandidateIdentity);
                Assert.Equal(FindingEvidenceRequestDecision.PendingExecution, Assert.Single(sourceReceipt.RequestDispositions!).Decision);
                Assert.DoesNotContain(requester.Id, DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(goal, DefaultAgents()).TaskIds);
                Assert.Empty(Closures());
                Assert.Empty(Retries());
                Assert.Equal(0, dispatchCalls);

                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
                Assert.Equal(2, executions.Count);
                Assert.Equal(expectedNew.Select(name => Format(Selection(name))).Order(StringComparer.Ordinal),
                    executions[1].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
                if (exhaustRemainder)
                {
                    Assert.Empty(Closures());
                    for (var tick = 0; tick < 2; tick++) driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
                    Assert.Equal(FindingRoundBatchSchedule.MaximumCompletedRuns, executions.Count);
                    Assert.Equal(executions[1], executions[2]);
                    Assert.Equal(1, ClosingSummary().Unavailable);
                    Assert.Single(escalations);
                    Assert.Empty(Retries());
                    Assert.Equal(0, dispatchCalls);
                    return;
                }

                var receipts = requester.VerificationHistory.Last().FindingEvidenceReceipts!;
                Assert.Equal(2, receipts.Count);
                var remainderReceipt = Assert.Single(receipts, receipt => receipt.ReceiptId != sourceReceipt.ReceiptId);
                Assert.Equal(FindingEvidenceRequestDecision.Executed, Assert.Single(remainderReceipt.RequestDispositions!).Decision);
                var retry = Assert.Single(Retries());
                Assert.Contains(sourceReceipt.ReceiptId, retry.Message);
                Assert.Contains(remainderReceipt.ReceiptId, retry.Message);
                var summary = ClosingSummary();
                Assert.Equal(1, summary.NewlyExecuted);
                Assert.Equal(0, summary.ReviewerCallsAvoided);
                Assert.Equal(1, dispatchCalls);
            }

            var next = EvidenceFindingWithRequest("Request the remaining evidence.", id: "remaining", category: FindingCategory.TestEvidence, classes: requested);
            if (firstRed)
            {
                // Reopen the Developer verification before simulating recovery on the same candidate.
                kernel.RetryTask(goal.Id, developer.Id, "Recover the failed evidence round without changing the candidate.");
                PassVerification(kernel, goal, developer, hasCommittedChanges: false);
                kernel.RetryTask(goal.Id, requester.Id, "Request the unexecuted selections under the same candidate.");
                RecordTesterRequest(kernel, goal, requester, next);
            }
            else FailReviewerNeedsWork(kernel, goal, requester, "remaining", findings: [next]);
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            var outcome = requester.VerificationHistory.Last().MergedReviewFindings!
                .Single(finding => finding.StableId == next.StableId).EvidenceOutcome!;
            if (recordNotRun && !firstRed)
            {
                Assert.Equal(2, executions.Count);
                Assert.Equal("reused-covered-green:composite", outcome.DecisionReason);
                Assert.Contains(sourceReceipt.ReceiptId, outcome.SourceReceiptIds!);
                Assert.Equal(2, outcome.SourceReceiptIds!.Count);
            }
            else if (expectedNew.Length == 0)
            {
                Assert.Single(executions);
                Assert.Equal("reused-covered-green:subset", outcome.DecisionReason);
                Assert.Equal(sourceReceipt.ReceiptId, outcome.ReceiptId);
            }
            else
            {
                Assert.Equal(2, executions.Count);
                Assert.Equal(expectedNew.Select(name => Format(Selection(name))).Order(StringComparer.Ordinal),
                    executions[1].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
                Assert.NotEqual(sourceReceipt.ReceiptId, outcome.ReceiptId);
            }
        }
        finally { Directory.Delete(artifactRoot, recursive: true); }
    }

    private static FindingEvidenceSelection Selection(string name) => new("Infrastructure.Tests", name);
    private static string Format(FindingEvidenceSelection selection) => $"{selection.TestProject}:{selection.TestClass}";

    private static void RecordTesterRequest(AgentOrchestratorKernel kernel, Goal goal, TaskSpec tester, ReviewFinding finding)
    {
        DispatchTask(kernel, goal, tester, "test");
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: test", "tests: deferred - focused evidence requested",
            "blockers: exact-blocker - focused evidence requested",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}", "touched_anchors: []",
            "model_fit: test/test - adequate - fixture - fixture", "skills: none", "confidence: high", "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id,
            new TaskVerificationRecord("test", "C:\\tmp", 1, output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true));
    }
}
