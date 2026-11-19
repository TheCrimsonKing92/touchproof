using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawningShardA")]
public sealed class ConductorDriverTestsFindingEvidenceReuse : IDisposable
{
    private readonly string _artifactRoot = InfrastructureTestSupport.CreateTempDirectory();

    [Xunit.Fact]
    public void CandidateOnlyRedInChangedTestFileRoutesToDeveloper()
    {
        const string candidateSha = "abc1234";
        const string failingTest =
            "ConductorDriverTestsFindingEvidenceReuse.CandidateOnlyRedInChangedTestFileRoutesToDeveloper";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        var finding = EvidenceFindingWithRequest(
            "The candidate-only red names a test changed by this candidate.",
            id: "candidate-only-red",
            category: FindingCategory.TestEvidence,
            classes: ["ConductorDriverTestsFindingEvidenceReuse"]);
        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, finding);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                var red = CandidateRedFindingEvidence(request, candidateSha, failingTest);
                return red with
                {
                    Summary = "candidate-only red evidence",
                    Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                    OutcomeReason = null
                };
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            getLandingFileScopes: _ =>
                ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsFindingEvidenceReuse.cs"],
            executionDirectory: InfrastructureTestSupport.FindRepositoryRoot());

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Contains(failingTest, retryMessage, StringComparison.Ordinal);
        Assert.Contains("receipt_id=", retryMessage, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CandidateOnlyRedOutsideChangedFilesRunsBaselineOnceThenRoutesToDeveloper()
    {
        const string candidateSha = "abc1234";
        const string failingTest = "GoalAcceptanceVerifierTests.CandidateOutsideChangedFiles";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        var finding = EvidenceFindingWithRequest(
            "The candidate-only red names a test outside this candidate.",
            id: "candidate-only-red-outside",
            category: FindingCategory.TestEvidence,
            classes: ["GoalAcceptanceVerifierTests"]);
        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, finding);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                var red = CandidateRedFindingEvidence(request, candidateSha, failingTest);
                return focusedRuns == 1
                    ? red with
                    {
                        Summary = "candidate-only red evidence",
                        Arms = red.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                        OutcomeReason = null
                    }
                    : red;
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            getLandingFileScopes: _ =>
                ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsFindingEvidenceReuse.cs"],
            executionDirectory: InfrastructureTestSupport.FindRepositoryRoot());

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
        Assert.Equal(developer.Id, retriedTaskId);
    }

    [Xunit.Theory]
    [Xunit.InlineData(FindingEvidenceArmDisposition.Inconclusive, FindingEvidenceOutcomeReason.BaselineInconclusive)]
    [Xunit.InlineData(FindingEvidenceArmDisposition.ApparatusFailure, FindingEvidenceOutcomeReason.ApparatusFailure)]
    public void CandidateRedWithUnusableBaselineEscalatesWithoutRetryingTester(
        FindingEvidenceArmDisposition baselineDisposition,
        FindingEvidenceOutcomeReason outcomeReason)
    {
        const string candidateSha = "abc1234";
        const string failingTest = "GoalAcceptanceVerifierTests.CandidateOutsideChangedFiles";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        var finding = EvidenceFindingWithRequest(
            "The candidate red has an unusable Baseline arm.",
            id: "candidate-red-unusable-baseline",
            category: FindingCategory.TestEvidence,
            classes: ["GoalAcceptanceVerifierTests"]);
        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, finding);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        string? escalation = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                var red = CandidateRedFindingEvidence(request, candidateSha, failingTest);
                if (focusedRuns != 1)
                {
                    return red;
                }

                return red with
                {
                    Arms = red.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Baseline
                        ? arm with
                        {
                            Disposition = baselineDisposition,
                            Passed = false,
                            Summary = "baseline apparatus failed"
                        }
                        : arm).ToArray(),
                    OutcomeReason = outcomeReason
                };
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            getLandingFileScopes: _ =>
                ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsFindingEvidenceReuse.cs"],
            writeEscalation: (_, _, message) => escalation = message,
            executionDirectory: InfrastructureTestSupport.FindRepositoryRoot());

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Null(retriedTaskId);
        Assert.Contains("Baseline execution failure", escalation, StringComparison.Ordinal);
        Assert.Contains("candidate-red-unusable-baseline", escalation, StringComparison.Ordinal);
        Assert.Contains(candidateSha, escalation, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void IdenticalRedRequestReusesReceiptWithoutAnotherExecution()
    {
        const string candidateSha = "abc1234";
        const string failingTest =
            "ConductorDriverTestsFindingEvidenceReuse.IdenticalRedRequestReusesReceiptWithoutAnotherExecution";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        var finding = EvidenceFindingWithRequest(
            "The unchanged candidate still has the same red receipt.",
            id: "reusable-red",
            category: FindingCategory.TestEvidence,
            classes: ["ConductorDriverTestsFindingEvidenceReuse"]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return CandidateOnlyRedWithExecutedClasses(
                    _artifactRoot,
                    request,
                    candidateSha,
                    failingTest,
                    ["ConductorDriverTestsFindingEvidenceReuse"]);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            getLandingFileScopes: _ =>
                ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsFindingEvidenceReuse.cs"],
            executionDirectory: InfrastructureTestSupport.FindRepositoryRoot());

        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, finding);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        PassVerification(kernel, goal, developer, hasCommittedChanges: false);
        kernel.RetryTask(goal.Id, tester.Id, "Reopen the same Tester finding without changing the candidate.");
        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, finding);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Contains(goal.Timeline, item =>
            item.Message.Contains("disposition=reused-red", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void PartialMultiClassRedReceiptDoesNotSuppressMissingExecution()
    {
        const string candidateSha = "abc1234";
        const string failingTest =
            "ConductorDriverTestsFindingEvidenceReuse.PartialMultiClassRedReceiptDoesNotSuppressMissingExecution";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        var finding = EvidenceFindingWithRequest(
            "The retained red receipt executed only one of two requested classes.",
            id: "partial-red-coverage",
            category: FindingCategory.TestEvidence,
            classes: ["ConductorDriverTestsFindingEvidenceReuse", "GoalAcceptanceVerifierTests"]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return CandidateOnlyRedWithExecutedClasses(
                    _artifactRoot,
                    request,
                    candidateSha,
                    failingTest,
                    ["ConductorDriverTestsFindingEvidenceReuse"]);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            getLandingFileScopes: _ =>
                ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsFindingEvidenceReuse.cs"],
            executionDirectory: InfrastructureTestSupport.FindRepositoryRoot());

        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, finding);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        PassVerification(kernel, goal, developer, hasCommittedChanges: false);
        kernel.RetryTask(goal.Id, tester.Id, "Replay the same request with incomplete retained coverage.");
        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, finding);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
    }

    [Xunit.Theory]
    [Xunit.InlineData("none")]
    [Xunit.InlineData("green")]
    [Xunit.InlineData("unsupported")]
    [Xunit.InlineData("inherited-red")]
    [Xunit.InlineData("missing-baseline")]
    [Xunit.InlineData("resumed-missing-baseline")]
    [Xunit.InlineData("missing-baseline-green")]
    [Xunit.InlineData("resumed-missing-baseline-green")]
    public void ReusedCandidateOnlyRedOutsideChangedFilesRunsBaselineOnceThenRoutesToDeveloper(string siblingOutcome)
    {
        const string candidateSha = "abc1234";
        const string failingTest =
            "ConductorDriverTestsFindingEvidenceReuse.ReusedCandidateOnlyRedOutsideChangedFilesRunsBaselineOnceThenRoutesToDeveloper";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        var finding = EvidenceFindingWithRequest(
            "A historical candidate-only red receipt needs Baseline attribution.",
            id: "reused-red-needs-baseline",
            category: FindingCategory.TestEvidence,
            classes: ["ConductorDriverTestsFindingEvidenceReuse"]);
        var focusedRequests = new List<string>();
        TaskId? retriedTaskId = null;
        var retries = 0;
        string? escalation = null;
        var greenRerun = siblingOutcome is "missing-baseline-green" or "resumed-missing-baseline-green";
        var missingBaseline = greenRerun || siblingOutcome is "missing-baseline" or "resumed-missing-baseline";
        var resumeRerun = siblingOutcome is "resumed-missing-baseline" or "resumed-missing-baseline-green";
        var interruptBeforeRerun = resumeRerun;
        FocusedEvidenceRunResult RunEvidence(string request)
        {
            focusedRequests.Add(request);
            return focusedRequests.Count switch
            {
                1 => CandidateOnlyRedWithExecutedClasses(_artifactRoot, request, candidateSha, failingTest,
                    ["ConductorDriverTestsFindingEvidenceReuse"]),
                2 => CandidateRedFindingEvidence(request, candidateSha, failingTest),
                3 when siblingOutcome == "unsupported" => new FocusedEvidenceRunResult(
                    request, Accepted: false, Passed: false, Summary: "Unsupported advisory project", Checks: [],
                    Rejection: new(FocusedEvidenceRejectionCode.UnsupportedProject, request, "Unsupported advisory project")),
                3 when siblingOutcome == "inherited-red" => InheritedRed(request),
                3 or 4 when missingBaseline => CandidateOnlyRedWithExecutedCount(request, candidateSha, "SiblingTests.Fails"),
                5 when missingBaseline && !greenRerun => new(request, false, false, "Advisory rerun produced no usable evidence", []),
                _ => RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha)
            };
        }

        FocusedEvidenceRunResult InheritedRed(string request)
        {
            var red = CandidateRedFindingEvidence(request, candidateSha, "SiblingTests.Fails");
            var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
            return red with { Arms = [candidate, candidate with { Arm = FindingEvidenceArm.Baseline, Sha = "baseline-sha" }] };
        }

        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, finding);
        var seedDriver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) => RunEvidence(request),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            getLandingFileScopes: _ =>
                ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTestsFindingEvidenceReuse.cs"],
            executionDirectory: InfrastructureTestSupport.FindRepositoryRoot());
        var seedBatch = FindingRoundBatchCoordinator.Identity(seedDriver, goal, tester)!;
        seedDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        PassVerification(kernel, goal, developer, hasCommittedChanges: false);
        kernel.RetryTask(goal.Id, tester.Id, "Replay the historical red receipt without changing the candidate.");
        var sibling = EvidenceFindingWithRequest("An unrelated focused request is still pending.", id: "green-sibling",
            category: FindingCategory.TestEvidence, project: "Core.Tests", classes: ["SiblingTests"])
            with { Severity = FindingSeverity.Advisory };
        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, siblingOutcome == "none" ? [finding] : [finding, sibling]);
        var replayDriver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) => RunEvidence(request),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retries++;
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
            {
                if (interruptBeforeRerun && message.Contains("disposition=candidate-rerun-requested;", StringComparison.Ordinal))
                {
                    interruptBeforeRerun = false;
                    throw new IOException("Interrupted before advisory candidate rerun.");
                }
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message);
            },
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            getLandingFileScopes: _ => missingBaseline ? [] : ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs"],
            writeEscalation: (_, _, message) => escalation = message,
            executionDirectory: missingBaseline ? _artifactRoot : InfrastructureTestSupport.FindRepositoryRoot());
        var replayBatch = FindingRoundBatchCoordinator.Identity(replayDriver, goal, tester)!;

        if (resumeRerun)
        {
            var interrupted = Assert.Throws<IOException>(() => replayDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive));
            Assert.Equal("Interrupted before advisory candidate rerun.", interrupted.Message);
            Assert.Equal(4, focusedRequests.Count);
            Assert.Equal(0, retries);
            Assert.Null(escalation);
            Assert.Contains(goal.Timeline, item => item.Message.Contains("disposition=baseline-arm-absent;", StringComparison.Ordinal));
        }
        replayDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(missingBaseline ? 5 : siblingOutcome == "none" ? 2 : 3, focusedRequests.Count);
        if (missingBaseline)
            Assert.Equal(focusedRequests[2], focusedRequests[4]);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Equal(1, retries);
        Assert.Null(escalation);
        if (greenRerun)
        {
            Assert.Single(goal.Timeline, item => item.Message.Contains("disposition=candidate-rerun-green;", StringComparison.Ordinal));
            Assert.Single(FindingRoundBatchAccounting.Read(goal, replayBatch),
                item => item.Kind == "executed" && item.FindingIds.Contains(sibling.StableId));
            var closure = Assert.Single(goal.Timeline.Where(item => item.TaskId == tester.Id &&
                item.Message.StartsWith($"finding-evidence batch-closed; batch_key={replayBatch.Key};", StringComparison.Ordinal)));
            Assert.Contains("Developer:", closure.Message, StringComparison.Ordinal);
            Assert.Contains("\"NewlyExecuted\":1", closure.Message, StringComparison.Ordinal);
            Assert.Contains("\"Covered\":1", closure.Message, StringComparison.Ordinal);
            Assert.Contains("\"Unavailable\":0", closure.Message, StringComparison.Ordinal);
        }
        if (!greenRerun && (missingBaseline || siblingOutcome is "unsupported" or "inherited-red"))
        {
            if (siblingOutcome == "unsupported")
            {
                var outcome = tester.VerificationHistory.Last().MergedReviewFindings!.Single(item => item.StableId == sibling.StableId);
                Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, outcome.EvidenceOutcome?.Reason);
            }
            else
            {
                var unavailable = Assert.Single(FindingRoundBatchAccounting.Read(goal, replayBatch),
                    item => item.Kind == "unavailable" && item.FindingIds.Contains(sibling.StableId));
                Assert.Contains(missingBaseline ? "Baseline execution failure" : "inherited from a RED Baseline arm", unavailable.Eligibility);
                if (missingBaseline)
                    Assert.Single(goal.Timeline, item => item.Message.Contains("disposition=candidate-rerun-unusable;", StringComparison.Ordinal));
            }
            Assert.NotEqual(seedBatch.RoundFingerprint, replayBatch.RoundFingerprint);
            Assert.True(FindingRoundBatchAccounting.IsClosed(goal, seedBatch));
            var closure = Assert.Single(goal.Timeline.Where(item => item.TaskId == tester.Id &&
                item.Message.StartsWith($"finding-evidence batch-closed; batch_key={replayBatch.Key};", StringComparison.Ordinal)));
            Assert.Contains("Developer:", closure.Message, StringComparison.Ordinal);
            Assert.Contains("\"BatchSize\":2", closure.Message, StringComparison.Ordinal);
            Assert.Contains("\"Covered\":1", closure.Message, StringComparison.Ordinal);
            Assert.Contains("\"Unavailable\":1", closure.Message, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void MissingExecutor_EscalatesOnceWithoutDispatchingWorker()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        var finding = EvidenceFindingWithRequest(
            "The executor is unavailable for the same request.",
            id: "missing-executor",
            category: FindingCategory.TestEvidence);
        var retries = 0;
        var starts = 0;
        var escalations = new List<string>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            dispatchAndStart: _ =>
            {
                starts++;
                return DispatchStartOutcome.Started();
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retries++;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            writeEscalation: (_, _, message) => escalations.Add(message));

        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, finding);
        var identity = FindingRoundBatchCoordinator.Identity(driver, goal, tester)!;
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);

        Assert.Equal(0, retries);
        Assert.Equal(0, starts);
        Assert.True(FindingRoundBatchAccounting.IsClosed(goal, identity));
        Assert.Contains("No focused evidence executor was configured", Assert.Single(escalations), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void TesterReopenedSameCandidateFindingReusesAndProjectsCompletedProcessReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer);
        var finding = EvidenceFindingWithRequest(
            "The process receipt is missing.",
            id: "tester-process-receipt",
            category: FindingCategory.TestEvidence);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        RecordTesterEvidenceOnlyFinding(kernel, goal, tester, finding);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        RecordTesterEvidenceOnlyFinding(
            kernel,
            goal,
            tester,
            finding with { Description = "The reopened finding still needs its completed process receipt." });
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var receipt = Assert.Single(tester.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .DistinctBy(candidate => candidate.ReceiptId));
        Assert.Contains(receipt.Arms!, arm =>
            arm is
            {
                Arm: FindingEvidenceArm.Candidate,
                Disposition: FindingEvidenceArmDisposition.Green,
                Accepted: true,
                Passed: true
            } && string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            receipt.ReceiptId,
            tester.VerificationHistory.Last().MergedReviewFindings!
                .Single(candidate => candidate.StableId == finding.StableId)
                .EvidenceOutcome?.ReceiptId);
        Assert.Contains(goal.Timeline, item =>
            item.Message.Contains("finding-evidence disposition=reused-covered-green", StringComparison.Ordinal));
        var projection = ReviewFindingContextProjector.Project(goal, tester, candidateSha);
        var packagedReceipt = Assert.Single(projection.ReceiptBodies);
        Assert.Contains(receipt.ReceiptId, System.Text.Encoding.UTF8.GetString(packagedReceipt.Bytes), StringComparison.Ordinal);
        using var ledger = System.Text.Json.JsonDocument.Parse(projection.LedgerBytes);
        var projectedFinding = Assert.Single(ledger.RootElement.GetProperty("findings").EnumerateArray());
        var projectedOutcome = projectedFinding.GetProperty("evidence_outcome");
        Assert.Equal(receipt.ReceiptId, projectedOutcome.GetProperty("receipt_id").GetString());
        Assert.Equal("valid-evidence", projectedOutcome.GetProperty("result_reason").GetString());
        var projectedReceiptBody = Assert.Single(projectedFinding.GetProperty("receipt_bodies").EnumerateArray());
        Assert.Equal(packagedReceipt.Sha256, projectedReceiptBody.GetProperty("sha256").GetString());
        Assert.Contains(packagedReceipt.Sha256, projection.ActiveBodyHashes);
    }

    [Xunit.Fact]
    public void EmptySelectionCannotSynthesizeAnHonouredReceiptFromUnrelatedHistory()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task);
        var finding = EvidenceFindingWithRequest("Seed actual evidence history", id: "seed");
        FailReviewerNeedsWork(kernel, goal, reviewer, "seed", findings: [finding]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) => RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha),
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, kind) => kernel.RetryTask(goalId, taskId, message, retryRoundKind: kind),
            recordFindingEvidenceRequest: (goalId, taskId, message) => kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, id, outcome, receipt) => kernel.RecordFindingEvidenceOutcome(goalId, taskId, id, outcome, receipt));
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.NotEmpty(reviewer.VerificationHistory.SelectMany(verification => verification.FindingEvidenceReceipts ?? []));
        Assert.False(ConductorDriver.TryResolveFindingEvidenceCoverage(
            reviewer, finding.EvidenceRequest! with { Selections = [] }, candidateSha, "basis",
            out var receipts, out _, out _));
        Assert.Empty(receipts);
    }

    [Xunit.Theory]
    [Xunit.InlineData("suppressed-budget")]
    [Xunit.InlineData("not-executed")]
    public void ExplicitNonExecutionCannotBecomeReusableGreenEvidence(string disposition)
    {
        var request = EvidenceFindingWithRequest("control", id: "control").EvidenceRequest!;
        var receipt = new FindingEvidenceReceipt("receipt", "candidate-a", request, true, true, "control",
            Arms: [new(FindingEvidenceArm.Candidate, "candidate-a", FindingEvidenceArmDisposition.Green, true, true, "control")],
            RequestDispositions: [new("control", "request-identity", disposition)]);
        Assert.False(ConductorDriver.IsReusableGreenFindingEvidenceReceipt(receipt, "candidate-a", "basis"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("legacy-no-arms")]
    [Xunit.InlineData("missing-custody")]
    [Xunit.InlineData("invalid-custody")]
    public void LegacyOrUntrustedReceiptCannotBecomeReusableGreenEvidence(string control)
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest("Establish a content-bound receipt.", id: "custody-source");
        FailReviewerNeedsWork(kernel, goal, reviewer, "custody source", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Equal(1, focusedRuns);
        var untrustedFinding = finding with
        {
            StableId = $"custody-{control}",
            Description = $"A newer {control} receipt must not authorize reuse."
        };
        FailReviewerNeedsWork(kernel, goal, reviewer, control, findings: [untrustedFinding]);
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(
            reviewer.LastVerification,
            out var untrustedRound,
            out _));
        var untrustedReceipt = control switch
        {
            "legacy-no-arms" => receipt with
            {
                ReceiptId = $"untrusted-{control}",
                Arms = null
            },
            "missing-custody" => receipt with
            {
                ReceiptId = $"untrusted-{control}",
                Arms = receipt.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
                    ? arm with { ReceiptArtifacts = null }
                    : arm).ToArray()
            },
            "invalid-custody" => receipt with
            {
                ReceiptId = $"untrusted-{control}",
                Arms = receipt.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
                    ? arm with
                    {
                        ReceiptArtifacts = arm.ReceiptArtifacts!
                            .Select(artifact => artifact with { Sha256 = new string('0', 64) })
                            .ToArray()
                    }
                    : arm).ToArray()
            },
            _ => throw new InvalidOperationException($"Unknown control '{control}'.")
        };
        untrustedReceipt = untrustedReceipt with
        {
            FindingRoundFingerprint = ConductorDriver.BuildFindingRoundFingerprint(reviewer, untrustedRound),
            RequestDispositions =
            [
                new FindingEvidenceRequestDisposition(
                    untrustedFinding.StableId,
                    receipt.RequestDispositions!.Single().RequestIdentity,
                    "executed-standalone")
            ]
        };
        kernel.RecordFindingEvidenceOutcome(
            goal.Id,
            reviewer.Id,
            untrustedFinding.StableId,
            new FindingEvidenceOutcome(
                Honoured: true,
                ReceiptId: untrustedReceipt.ReceiptId,
                ResultReason: FindingEvidenceOutcomeReason.ValidEvidence),
            untrustedReceipt);
        kernel.RetryTask(goal.Id, reviewer.Id, "Continue after recording untrusted evidence.");

        var repeated = finding with
        {
            StableId = $"custody-repeat-{control}",
            Description = $"The request after {control} evidence must execute again."
        };
        FailReviewerNeedsWork(kernel, goal, reviewer, $"repeat after {control}", findings: [repeated]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
    }

    [Xunit.Fact]
    public void ChangedFindingRoundAtSameCandidateReusesCandidateAndRequestBoundReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The first finding round needs focused evidence.",
            id: "same-sha-new-round");
        FailReviewerNeedsWork(kernel, goal, reviewer, "first finding round", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var repeatedFinding = finding with { Description = "A changed finding round still needs focused evidence." };
        FailReviewerNeedsWork(kernel, goal, reviewer, "changed finding round", findings: [repeatedFinding]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var receipts = reviewer.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .DistinctBy(receipt => receipt.ReceiptId)
            .ToArray();
        var receipt = Assert.Single(receipts);
        Assert.Equal(candidateSha, receipt.CandidateSha);
        Assert.Equal(
            receipt.ReceiptId,
            reviewer.VerificationHistory.Last().MergedReviewFindings!.Single().EvidenceOutcome?.ReceiptId);
        Assert.Equal(
            receipt.ReceiptId,
            Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!).ReceiptId);
        Assert.Contains(goal.Timeline, item =>
            item.Message.Contains("finding-evidence disposition=reused-covered-green", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void UnmatchedFindingRoundAtSameCandidateReusesRequestBoundReceipt(bool sameAnchor)
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The first finding round needs focused evidence.",
            id: "same-sha-original-finding");
        FailReviewerNeedsWork(kernel, goal, reviewer, "first finding round", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var unmatchedFinding = finding with
        {
            StableId = "same-sha-unmatched-finding",
            Location = sameAnchor ? finding.Location : finding.Location with { Region = "A separate evidence obligation" },
            Description = "A new stable id requests the same focused evidence."
        };
        FailReviewerNeedsWork(kernel, goal, reviewer, "unmatched finding round", findings: [unmatchedFinding]);
        var mergedId = sameAnchor ? finding.StableId : unmatchedFinding.StableId;
        Assert.Contains(reviewer.LastVerification!.MergedReviewFindings!,
            item => item.StableId == mergedId);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var latestFinding = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == mergedId);
        Assert.True(latestFinding.EvidenceOutcome?.Honoured);
        Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, latestFinding.EvidenceOutcome?.ResultReason);
        Assert.Contains(goal.Timeline, item =>
            item.Message.Contains("finding-evidence disposition=reused-covered-green", StringComparison.Ordinal) &&
            item.Message.Contains($"finding_id={mergedId}", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void UnionRequestAtSameCandidateReusesConstituentGreenReceipts()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var first = EvidenceFindingWithRequest(
            "The first constituent needs focused evidence.",
            id: "union-first",
            classes: ["ConductorDriverTests"]);
        var second = EvidenceFindingWithRequest(
            "The second constituent needs focused evidence.",
            id: "union-second",
            classes: ["GoalAcceptanceVerifierTests"]);
        var union = EvidenceFindingWithRequest(
            "The compatible union should reuse both green constituents.",
            id: "union-request",
            classes: ["ConductorDriverTests", "GoalAcceptanceVerifierTests"]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        FailReviewerNeedsWork(kernel, goal, reviewer, "first constituent", findings: [first]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(kernel, goal, reviewer, "second constituent", findings: [second]);
        Assert.Contains(reviewer.LastVerification!.MergedReviewFindings!, finding =>
            finding.StableId == first.StableId && finding.State == ReviewFindingState.Open);
        var secondBatch = FindingRoundBatchCoordinator.Identity(driver, goal, reviewer)!;
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var member = Assert.Single(FindingRoundBatchAccounting.Read(goal, secondBatch).Where(item => item.Kind == "member"));
        Assert.Equal(second.StableId, Assert.Single(member.FindingIds));
        Assert.True(FindingRoundBatchAccounting.IsClosed(goal, secondBatch));
        FailReviewerNeedsWork(kernel, goal, reviewer, "union request", findings: [union]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
        var latestFinding = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == union.StableId);
        Assert.True(latestFinding.EvidenceOutcome?.Honoured);
        Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence, latestFinding.EvidenceOutcome?.ResultReason);
        Assert.Equal("reused-covered-green:composite", latestFinding.EvidenceOutcome?.DecisionReason);
        Assert.Equal(2, latestFinding.EvidenceOutcome?.SourceReceiptIds?.Count);
        Assert.Contains(
            latestFinding.EvidenceOutcome!.ReceiptId!,
            latestFinding.EvidenceOutcome.SourceReceiptIds!);
    }

    [Xunit.Fact]
    public void NarrowerRequestAtSameCandidateReusesSingleCoveringGreenReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var broader = EvidenceFindingWithRequest(
            "The broader request establishes green evidence.",
            id: "broader-request",
            classes: ["ConductorDriverTests", "GoalAcceptanceVerifierTests"]);
        var narrower = EvidenceFindingWithRequest(
            "The narrower request is covered by the broader receipt.",
            id: "narrower-request",
            classes: ["ConductorDriverTests"]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        FailReviewerNeedsWork(kernel, goal, reviewer, "broader request", findings: [broader]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(kernel, goal, reviewer, "narrower request", findings: [narrower]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == narrower.StableId)
            .EvidenceOutcome?.Honoured);
    }

    [Xunit.Fact]
    public void UnionRequestWithUncoveredSelectionDoesNotReuseConstituentReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var first = EvidenceFindingWithRequest(
            "The only covered constituent needs focused evidence.",
            id: "uncovered-first",
            classes: ["ConductorDriverTests"]);
        var union = EvidenceFindingWithRequest(
            "The union has an uncovered constituent and must run.",
            id: "uncovered-union",
            classes: ["ConductorDriverTests", "GoalAcceptanceVerifierTests"]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        FailReviewerNeedsWork(kernel, goal, reviewer, "covered constituent", findings: [first]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        FailReviewerNeedsWork(kernel, goal, reviewer, "uncovered union", findings: [union]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
    }

    [Xunit.Fact]
    public void ChangedFindingRequestAtSameCandidateDoesNotReusePriorReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The first request needs focused evidence.",
            id: "same-sha-changed-request",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "first request", findings: [finding]);
        var focusedRequests = new List<string>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRequests.Add(request);
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var changedRequest = EvidenceFindingWithRequest(
            "The changed request needs different evidence.",
            id: finding.StableId,
            classes: ["GoalAcceptanceVerifierTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "changed request", findings: [changedRequest]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRequests.Count);
        Assert.Contains("Infrastructure.Tests:ConductorDriverTests", focusedRequests);
        Assert.Contains("Infrastructure.Tests:GoalAcceptanceVerifierTests", focusedRequests);
        Assert.Equal(
            2,
            reviewer.VerificationHistory
                .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
                .DistinctBy(receipt => receipt.ReceiptId)
                .Count());
    }

    [Xunit.Fact]
    public void NonGreenReceiptAtSameCandidateDoesNotSuppressRerun()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The request encountered an apparatus failure.",
            id: "same-sha-apparatus-failure");
        FailReviewerNeedsWork(kernel, goal, reviewer, "first attempt", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: true,
                    Passed: false,
                    Summary: "selection apparatus failed",
                    Checks: [],
                    OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);

        Assert.Equal(2, focusedRuns);
        Assert.All(
            reviewer.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? []),
            receipt => Assert.False(receipt.Passed));
    }

    [Xunit.Fact]
    public void RequestedCoverageWithoutMatchingExecutedCoverageDoesNotReuse()
    {
        const string candidateSha = "abc1234";
        var artifactRoot = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                PassVerification(kernel, goal, task);
            }

            var source = EvidenceFindingWithRequest(
                "The request names two classes but retained execution covers only one.",
                id: "coverage-source",
                classes: ["ConductorDriverTests", "GoalAcceptanceVerifierTests"]);
            var uncovered = EvidenceFindingWithRequest(
                "The class omitted by the retained execution must run.",
                id: "coverage-uncovered",
                classes: ["GoalAcceptanceVerifierTests"]);
            var focusedRuns = 0;
            var driver = MakeDriver(
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
                runFocusedEvidence: (_, request) =>
                {
                    focusedRuns++;
                    return RetainedEvidenceWithExecutedClasses(
                        artifactRoot,
                        request,
                        candidateSha,
                        ["ConductorDriverTests"]);
                },
                dispatchAndStart: _ => DispatchStartOutcome.Started(),
                retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                    kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
                recordFindingEvidenceRequest: (goalId, taskId, message) =>
                    kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            FailReviewerNeedsWork(kernel, goal, reviewer, "coverage source", findings: [source]);
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
            FailReviewerNeedsWork(kernel, goal, reviewer, "uncovered selection", findings: [uncovered]);
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(2, focusedRuns);
        }
        finally
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [Xunit.Fact]
    public void NewestReceiptPerSelectionRemainsAuthoritativeAcrossCoverageReuse()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        var source = EvidenceFindingWithRequest(
            "Initial green coverage.",
            id: "authority-source",
            classes: ["GoalAcceptanceVerifierTests", "ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "initial green", findings: [source]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var green = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);

        var redFinding = EvidenceFindingWithRequest(
            "A newer RED applies only to one selection.",
            id: "authority-red",
            classes: ["GoalAcceptanceVerifierTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "newer red", findings: [redFinding]);
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(
            reviewer.LastVerification,
            out var redRound,
            out _));
        var red = green with
        {
            ReceiptId = "newer-red-receipt",
            Request = redFinding.EvidenceRequest!,
            Passed = false,
            Arms = green.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
                ? arm with { Disposition = FindingEvidenceArmDisposition.Red, Passed = false }
                : arm).ToArray(),
            RequestDispositions =
            [
                new FindingEvidenceRequestDisposition(
                    redFinding.StableId,
                    "Infrastructure.Tests:GoalAcceptanceVerifierTests",
                    "executed-standalone")
            ],
            FindingRoundFingerprint = ConductorDriver.BuildFindingRoundFingerprint(reviewer, redRound)
        };
        kernel.RecordFindingEvidenceOutcome(
            goal.Id,
            reviewer.Id,
            redFinding.StableId,
            new FindingEvidenceOutcome(
                Honoured: true,
                ReceiptId: red.ReceiptId,
                ResultReason: FindingEvidenceOutcomeReason.CandidateRed),
            red);
        kernel.RetryTask(goal.Id, reviewer.Id, "Continue after recording newer red evidence.");

        var stillGreen = EvidenceFindingWithRequest(
            "The unaffected selection can reuse its green.",
            id: "authority-green-selection",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "unaffected selection", findings: [stillGreen]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(1, focusedRuns);

        var invalidated = EvidenceFindingWithRequest(
            "The newer RED selection must execute again.",
            id: "authority-invalidated-selection",
            classes: ["GoalAcceptanceVerifierTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "invalidated selection", findings: [invalidated]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(2, focusedRuns);

        var restored = EvidenceFindingWithRequest(
            "The later authoritative green restores reuse.",
            id: "authority-restored-selection",
            classes: ["GoalAcceptanceVerifierTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "restored selection", findings: [restored]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(2, focusedRuns);
    }

    [Xunit.Fact]
    public void ChangedExecutionBasisDoesNotReuseCoveredReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var basisMarker = "basis-a";
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            getFindingEvidenceEngineSettings: _ => BasisSettings(basisMarker));

        var source = EvidenceFindingWithRequest(
            "Establish evidence under the first basis.",
            id: "basis-source",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "basis source", findings: [source]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        basisMarker = "basis-b";
        var repeated = source with { StableId = "basis-repeat", Description = "The basis changed." };
        FailReviewerNeedsWork(kernel, goal, reviewer, "changed basis", findings: [repeated]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
    }

    [Xunit.Fact]
    public void CanonicalEquivalentNewFindingAndSubsetReuseWhileOnlyUncoveredSelectionRuns()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var focusedRequests = new List<string>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRequests.Add(request);
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        var initial = EvidenceFindingWithRequest(
            "Initial focused selection.",
            id: "canonical-source",
            classes: ["GoalAcceptanceVerifierTests", "ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "initial", findings: [initial]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var sourceReceipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);

        var equivalent = EvidenceFindingWithRequest(
            "Equivalent alias and order under a new finding.",
            id: "canonical-equivalent",
            project: "Mcg.AgentOrchestrator.Infrastructure.Tests",
            classes: ["ConductorDriverTests", "GoalAcceptanceVerifierTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "equivalent", findings: [equivalent]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(focusedRequests);

        var subset = EvidenceFindingWithRequest(
            "Covered subset under another finding.",
            id: "canonical-subset",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "subset", findings: [subset]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(focusedRequests);
        var subsetOutcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(finding => finding.StableId == subset.StableId)
            .EvidenceOutcome!;
        Assert.Equal(sourceReceipt.ReceiptId, subsetOutcome.ReceiptId);
        Assert.Equal("Infrastructure.Tests:ConductorDriverTests", subsetOutcome.RequestedSelectionIdentity);
        Assert.Equal("reused-covered-green:subset", subsetOutcome.DecisionReason);
        Assert.Equal([sourceReceipt.ReceiptId], subsetOutcome.SourceReceiptIds);

        var partial = EvidenceFindingWithRequest(
            "One covered and one genuinely uncovered class.",
            id: "canonical-partial",
            classes: ["ConductorDriverTests", "WorkerProcessJobsTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "partial", findings: [partial]);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRequests.Count);
        Assert.Equal("Infrastructure.Tests:WorkerProcessJobsTests", focusedRequests[1]);
        var partialOutcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(finding => finding.StableId == partial.StableId)
            .EvidenceOutcome!;
        Assert.Equal("executed-uncovered-after-reuse:partial", partialOutcome.DecisionReason);
        Assert.Equal(2, partialOutcome.SourceReceiptIds!.Count);
    }

    internal static FocusedEvidenceRunResult RetainedEvidenceWithExecutedClasses(
        string artifactRoot,
        string request,
        string candidateSha,
        IReadOnlyList<string>? executedClasses = null)
    {
        var source = DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red, candidateSha);
        executedClasses ??= request
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(selection => selection.Split(':', 2)[1])
            .ToArray();
        var trxPath = Path.Combine(artifactRoot, $"{Guid.NewGuid():N}.trx");
        XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var tests = executedClasses.Select((testClass, index) => new
        {
            Id = $"test-{index}",
            ClassName = $"Mcg.AgentOrchestrator.Infrastructure.Tests.{testClass}"
        }).ToArray();
        new XDocument(
            new XElement(
                trx + "TestRun",
                new XElement(
                    trx + "TestDefinitions",
                    tests.Select(test => new XElement(
                        trx + "UnitTest",
                        new XAttribute("id", test.Id),
                        new XElement(
                            trx + "TestMethod",
                            new XAttribute("className", test.ClassName),
                            new XAttribute("name", "Runs"))))),
                new XElement(
                    trx + "Results",
                    tests.Select(test => new XElement(
                        trx + "UnitTestResult",
                        new XAttribute("testId", test.Id),
                        new XAttribute("outcome", "Passed")))),
                new XElement(
                    trx + "ResultSummary",
                    new XElement(
                        trx + "Counters",
                        new XAttribute("total", tests.Length),
                        new XAttribute("executed", tests.Length),
                        new XAttribute("passed", tests.Length),
                        new XAttribute("failed", 0)))))
            .Save(trxPath);
        var sourceCandidate = source.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var candidateCheck = sourceCandidate.Checks.Single() with
        {
            ArtifactsPath = artifactRoot,
            TestResultPaths = [trxPath],
            ExecutedTestCount = executedClasses.Count
        };
        var candidate = sourceCandidate with { Checks = [candidateCheck] };
        return source with
        {
            Checks = [candidateCheck],
            Arms = source.Arms.Select(arm =>
                arm.Arm == FindingEvidenceArm.Candidate ? candidate : arm).ToArray()
        };
    }

    private static FocusedEvidenceRunResult CandidateOnlyRedWithExecutedCount(
        string request,
        string candidateSha,
        string failingTestIdentity)
    {
        var source = CandidateRedFindingEvidence(request, candidateSha, failingTestIdentity);
        var check = source.Checks.Single() with { ExecutedTestCount = 1 };
        var candidate = source.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate) with
        {
            Checks = [check]
        };
        return source with
        {
            Summary = "candidate-only red evidence with an executed test",
            Checks = [check],
            Arms = [candidate],
            OutcomeReason = null
        };
    }

    private static FocusedEvidenceRunResult CandidateOnlyRedWithExecutedClasses(
        string artifactRoot,
        string request,
        string candidateSha,
        string failingTestIdentity,
        IReadOnlyList<string> executedClasses)
    {
        var source = CandidateOnlyRedWithExecutedCount(request, candidateSha, failingTestIdentity);
        var trxPath = Path.Combine(artifactRoot, $"{Guid.NewGuid():N}.trx");
        XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var tests = executedClasses.Select((testClass, index) => new
        {
            Id = $"red-test-{index}",
            ClassName = $"Mcg.AgentOrchestrator.Infrastructure.Tests.{testClass}"
        }).ToArray();
        new XDocument(
            new XElement(
                trx + "TestRun",
                new XElement(
                    trx + "TestDefinitions",
                    tests.Select(test => new XElement(
                        trx + "UnitTest",
                        new XAttribute("id", test.Id),
                        new XElement(
                            trx + "TestMethod",
                            new XAttribute("className", test.ClassName),
                            new XAttribute("name", "Runs"))))),
                new XElement(
                    trx + "Results",
                    tests.Select(test => new XElement(
                        trx + "UnitTestResult",
                        new XAttribute("testId", test.Id),
                        new XAttribute("outcome", "Failed")))),
                new XElement(
                    trx + "ResultSummary",
                    new XElement(
                        trx + "Counters",
                        new XAttribute("total", tests.Length),
                        new XAttribute("executed", tests.Length),
                        new XAttribute("passed", 0),
                        new XAttribute("failed", tests.Length)))))
            .Save(trxPath);
        var sourceCandidate = source.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var candidateCheck = sourceCandidate.Checks.Single() with
        {
            ArtifactsPath = artifactRoot,
            TestResultPaths = [trxPath],
            ExecutedTestCount = executedClasses.Count
        };
        return source with
        {
            Checks = [candidateCheck],
            Arms = [sourceCandidate with { Checks = [candidateCheck] }]
        };
    }

    private static AcceptanceGateEngineSettings BasisSettings(string marker) =>
        new()
        {
            MtpInvocations =
            [
                new AcceptanceMtpInvocation
                {
                    Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    ExecutablePathTemplate = marker,
                    Arguments = ["--marker", marker]
                }
            ]
        };

    private static void RecordTesterEvidenceOnlyFinding(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        params ReviewFinding[] findings)
    {
        DispatchTask(kernel, goal, tester, "test");
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: test",
            "tests: deferred - focused evidence requested",
            "blockers: exact-blocker - focused evidence requested",
            $"findings: {System.Text.Json.JsonSerializer.Serialize(findings)}",
            "touched_anchors: []",
            "model_fit: test/test - adequate - fixture - fixture",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            tester.Id,
            new TaskVerificationRecord(
                "test", "C:\\tmp", 1, output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true));
    }

    [Xunit.Fact]
    public void ReviewerCorrectnessFindingWithEvidenceRequestReopensDeveloperBeforeEvidenceRuns()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task.Id == developer.Id);
        }

        var finding = EvidenceFindingWithRequest(
            "The candidate fails a correctness invariant.",
            id: "reviewer-correctness-finding",
            category: FindingCategory.Correctness);
        FailReviewerNeedsWork(kernel, goal, reviewer, "correctness finding", findings: [finding]);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return RetainedEvidenceWithExecutedClasses(_artifactRoot, request, candidateSha);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(developer.Id, retriedTaskId);
    }

    public void Dispose() => Directory.Delete(_artifactRoot, recursive: true);
}
