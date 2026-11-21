using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: inline evidence resolution with goal-local source and attempt files.
public sealed class ReviewerSelectorCorrectionBatchTests
{
    private const string DeclaredClass = "WorkerDispatchPlannerHandoffTests";
    private const string Project = "Infrastructure.Tests";

    [Fact]
    public void ValidBatchMemberKeepsItsCorrectionOpportunity()
    {
        using var fixture = new Scenario();
        var requester = fixture.Reviewer.Id;
        var missing = Finding("first", "AAbsentTests");
        var valid = Finding("other", DeclaredClass);
        fixture.Submit([missing, valid]);

        Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
        Assert.Equal($"{Project}:AAbsentTests; {Project}:{DeclaredClass}", Assert.Single(fixture.Executions));
        Assert.Empty(fixture.Corrections());

        fixture.Reload();
        fixture.Advance();

        Assert.Equal(GoalStatus.Active, fixture.Goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Reviewer.Status);
        Assert.Equal($"{Project}:FullyQualifiedName~{DeclaredClass}", fixture.Executions[1]);
        var first = Assert.Single(fixture.Corrections());
        Assert.Equal(requester, first.Task);
        Assert.Equal(new[] { "first" }, first.Findings);
        Assert.Contains("AAbsentTests", Assert.Single(first.Diagnostics));

        fixture.Submit([missing with { State = ReviewFindingState.Resolved, EvidenceRequest = null },
            Finding("other", "WorkerDispatchTests")]);
        var verification = fixture.Reviewer.LastVerification!;
        Assert.Equal(ReviewFindingState.Resolved,
            Assert.Single(verification.MergedReviewFindings!, finding => finding.StableId == missing.StableId).State);
        fixture.Advance();

        Assert.Equal(GoalStatus.Active, fixture.Goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Reviewer.Status);
        Assert.Equal(new[] { requester, requester }, fixture.Retries);
        Assert.Equal($"{Project}:FullyQualifiedName~WorkerDispatchTests", fixture.Executions[2]);
        Assert.Collection(fixture.Corrections(),
            correction => Assert.Equal(new[] { "first" }, correction.Findings),
            correction =>
            {
                Assert.Equal(requester, correction.Task);
                Assert.Equal(new[] { "other" }, correction.Findings);
                Assert.Contains(DeclaredClass, Assert.Single(correction.Diagnostics));
            });
    }

    private static ReviewFinding Finding(string id, string selector) =>
        new(id, ReviewFindingState.Open, new("tests/Guard.cs", id), "The required test evidence is missing.",
            FindingSeverity.Blocking, FindingCategory.TestEvidence,
            new FindingEvidenceRequest([new(Project, $"FullyQualifiedName~{selector}")]));

    private sealed class Scenario : IDisposable
    {
        private const string Candidate = "df1cbfc3a1535e3a062ab7fdda0b114c7653c716";
        private static readonly CandidateIdentity Identity = new("fixture-patch", "fixture-base", "fixture-manifest");
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly string _worktree;
        private ConductorDriver _driver;
        private DateTimeOffset _submissionTime = DateTimeOffset.UtcNow;
        private int _tick;

        internal Scenario()
        {
            (Kernel, var goal) = ReviewGoalWithoutTester();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            PassVerification(Kernel, goal, developer, hasCommittedChanges: true);
            Kernel.RecordDispatchResultCommit(goal.Id, developer.Id, Candidate);
            _worktree = GoalWorktrees.WorktreePath(_root, goal.Id);
            var directory = Path.Combine(_worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(_worktree, ".git"), "gitdir: fixture");
            File.WriteAllText(Path.Combine(directory, "WorkerDispatchTests.cs"),
                $"namespace Fixture; public class {DeclaredClass} {{ [Fact] public void ReadyBatch() {{}} }}");
            _driver = CreateDriver();
        }

        private AgentOrchestratorKernel Kernel { get; set; }
        internal Goal Goal => Kernel.Goals.Single();
        internal TaskSpec Reviewer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        internal List<string> Executions { get; } = [];
        internal List<TaskId> Retries { get; } = [];

        internal void Submit(IReadOnlyList<ReviewFinding> findings)
        {
            _submissionTime = _submissionTime.AddMinutes(1);
            var reviewer = Reviewer;
            var policy = ConductorAutonomyPolicy.Permissive;
            Kernel.RecordTaskDispatch(Goal.Id, reviewer.Id, new("fixture-reviewer", "review", _worktree, _submissionTime,
                BaseCommit: Candidate, CandidateIdentity: Identity,
                ReviewRetryCap: ReviewRetryCapReceipt.Create(Goal, policy.ReviewAutoRetryStopRound, policy.ReviewAutoRetryLifetimeMultiplier),
                FindingEvidenceReviewContext: FindingEvidenceReviewContext.Capture(Goal, reviewer, Candidate, _submissionTime)));
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - acceptance", "commit: none",
                "blockers: none", $"findings: {JsonSerializer.Serialize(findings)}", "touched_anchors: []", "verdict: needs-work",
                "model_fit: fixture/model - adequate - review", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, reviewer.Id, new("review", _worktree, 0, output, "", _submissionTime.AddSeconds(1),
                WorkerResultPresent: true, ReviewedCommit: Candidate, CandidateIdentity: Identity, DispatchStartedAt: _submissionTime));
            Assert.Null(Reviewer.LastVerification!.ReviewFindingContractViolation);
            Assert.True(ConductorDriver.UsesQualifiedReviewerContinuation(Goal, Reviewer));
            Assert.Equal(WorkTaskStatus.Failed, Reviewer.Status);
        }

        internal ConductorAdvanceResult Advance()
        {
            _driver.BeginTick(Kernel, ++_tick);
            return _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
        }

        internal void Reload()
        {
            var snapshot = JsonSerializer.Serialize(Kernel.ExportSnapshot());
            Kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(snapshot)!);
            _driver = CreateDriver();
        }

        internal IEnumerable<(TaskId? Task, string?[] Findings, string?[] Diagnostics)> Corrections()
        {
            foreach (var item in Goal.Timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                         item.Message.StartsWith("finding-evidence batch-closed;", StringComparison.Ordinal)))
            {
                using var summary = JsonDocument.Parse(item.Message[item.Message.IndexOf('{')..]);
                if (!summary.RootElement.TryGetProperty("SelectorCorrection", out var correction)) continue;
                yield return (item.TaskId,
                    correction.GetProperty("Findings").EnumerateArray().Select(finding => finding.GetProperty("FindingId").GetString()).ToArray(),
                    correction.GetProperty("Failures").EnumerateArray().Select(failure => failure.GetProperty("Diagnostic").GetString()).ToArray());
            }
        }

        private ConductorDriver CreateDriver() => MakeDriver(
            getFacts: _ => new(WorkspaceExists: true),
            executionDirectory: _root,
            getLandingFileScopes: _ => [],
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
            resolveFindingEvidenceSiblingClasses: (_, _, _) => [],
            focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(_root, "attempts"), TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
            runFocusedEvidence: (goal, request) =>
            {
                Executions.Add(request);
                if (!GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(request, new(), _worktree, out _, out _, out var rejection))
                    return new(request, false, false, rejection.Detail, [], Rejection: rejection);

                return ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_root, request, Candidate, [DeclaredClass]);
            },
            retryTaskWithCause: (goal, task, feedback, kind, cause) =>
            {
                Retries.Add(task);
                return Kernel.RetryTaskAutomatically(goal, task, feedback, retryRoundKind: kind, retryCause: cause);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Deferred("Reviewer is ready for correction."),
            startRecordedDispatches: _ => throw new InvalidOperationException("Fixture must never launch a worker."),
            recordFindingEvidenceRequest: (goal, task, message) => Kernel.RecordFindingEvidenceRequest(goal, task, message),
            recordFindingEvidenceRun: (goal, task, message) => Kernel.RecordFindingEvidenceRun(goal, task, message),
            recordFindingEvidenceRequestDispositions: (goal, task, dispositions) => Kernel.RecordFindingEvidenceRequestDispositions(goal, task, dispositions),
            recordFindingEvidenceOutcome: (goal, task, id, outcome, receipt) => Kernel.RecordFindingEvidenceOutcome(goal, task, id, outcome, receipt));

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
