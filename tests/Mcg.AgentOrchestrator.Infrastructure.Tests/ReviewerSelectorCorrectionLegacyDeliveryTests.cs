using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: inline evidence execution and goal-local attempt files.
public sealed class ReviewerSelectorCorrectionLegacyDeliveryTests
{
    [Fact]
    public void SecondUnresolvableSubmissionPreservesLegacyReviewerDelivery()
    {
        const string candidate = "df1cbfc3a1535e3a062ab7fdda0b114c7653c716";
        const string findingId = "missing-selector";
        var (kernel, goal) = ReviewGoalWithoutTester();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        var policy = ConductorAutonomyPolicy.Permissive;
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, candidate);

        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var worktree = GoalWorktrees.WorktreePath(root, goal.Id);
            var testDirectory = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(testDirectory);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            File.WriteAllText(Path.Combine(testDirectory, "PresentTests.cs"),
                "public class PresentTests { [Fact] public void Runs() {} }");

            var retries = new List<TaskId>();
            var rejections = new List<FocusedEvidenceRejectionCode>();
            var driver = MakeDriver(
                getFacts: _ => new(WorkspaceExists: true), executionDirectory: root, getLandingFileScopes: _ => [],
                getPreReviewEvidenceContext: _ => NoPreReviewContext(candidate),
                resolveFindingEvidenceSiblingClasses: (_, _, _) => [],
                focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(root, "attempts"), TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
                runFocusedEvidence: (unusedGoal, request) =>
                {
                    Assert.False(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
                        request, new(), worktree, out _, out _, out var rejection));
                    rejections.Add(rejection.Code);
                    return new(request, false, false, rejection.Detail, [], Rejection: rejection);
                },
                retryTaskWithCause: (goalId, taskId, feedback, kind, cause) =>
                {
                    retries.Add(taskId);
                    return kernel.RetryTaskAutomatically(goalId, taskId, feedback, retryRoundKind: kind, retryCause: cause);
                },
                dispatchAndStart: _ => DispatchStartOutcome.Deferred("Fixture holds dispatch."),
                startRecordedDispatches: _ => throw new InvalidOperationException("Fixture must never launch a worker."),
                recordFindingEvidenceRequest: (goalId, taskId, message) => kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                recordFindingEvidenceRun: (goalId, taskId, message) => kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                recordFindingEvidenceRequestDispositions: (goalId, taskId, items) =>
                    kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, items),
                recordFindingEvidenceOutcome: (goalId, taskId, id, outcome, receipt) =>
                    kernel.RecordFindingEvidenceOutcome(goalId, taskId, id, outcome, receipt));

            var firstDispatch = DateTimeOffset.UtcNow.AddMinutes(1);
            Submit("FullyQualifiedName~FirstMissingTests", new("fixture-patch", "fixture-base", "fixture-manifest"), firstDispatch);
            Assert.True(ConductorDriver.UsesQualifiedReviewerContinuation(goal, reviewer));
            driver.BeginTick(kernel, 1);
            driver.AdvanceOnce(goal, policy);
            Assert.Equal(reviewer.Id, Assert.Single(retries));
            Assert.Single(goal.Timeline, item => item.TaskId == reviewer.Id &&
                item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                item.Message.Contains("\"SelectorCorrection\":", StringComparison.Ordinal));

            Submit("FullyQualifiedName~SecondMissingTests", null, firstDispatch.AddMinutes(1));
            Assert.False(ConductorDriver.UsesQualifiedReviewerContinuation(goal, reviewer));
            driver.BeginTick(kernel, 2);
            driver.AdvanceOnce(goal, policy);

            Assert.Collection(rejections,
                first => Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, first),
                second => Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, second));
            Assert.Equal(GoalStatus.Active, goal.Status);
            Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
            Assert.Equal(new[] { reviewer.Id, reviewer.Id }, retries);
            Assert.Equal(1, FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
                goal.Timeline, reviewer.Id, candidate, findingId));

            void Submit(string selector, CandidateIdentity? identity, DateTimeOffset dispatchAt)
            {
                var finding = EvidenceFindingWithRequest("The required test evidence is missing.", id: findingId, classes: [selector]);
                kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new("fixture-reviewer", "review", worktree, dispatchAt,
                    BaseCommit: candidate, CandidateIdentity: identity,
                    FindingEvidenceReviewContext: FindingEvidenceReviewContext.Capture(goal, reviewer, candidate, dispatchAt)));
                var output = string.Join(Environment.NewLine,
                    "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - acceptance", "commit: none",
                    "blockers: none", $"findings: {JsonSerializer.Serialize(new[] { finding })}", "touched_anchors: []", "verdict: needs-work",
                    "model_fit: fixture/model - adequate - review", "skills: none", "confidence: high", "END_WORKER_RESULT");
                kernel.RecordTaskVerification(goal.Id, reviewer.Id, new("review", worktree, 0, output, "", dispatchAt.AddSeconds(1),
                    WorkerResultPresent: true, ReviewedCommit: candidate, CandidateIdentity: identity, DispatchStartedAt: dispatchAt));
                Assert.Null(reviewer.LastVerification!.ReviewFindingContractViolation);
                Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
            }
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }
}
