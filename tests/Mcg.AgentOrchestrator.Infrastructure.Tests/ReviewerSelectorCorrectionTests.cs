using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: inline evidence resolution and goal-local source and attempt files.
public sealed class ReviewerSelectorCorrectionTests
{
    private const string InvalidSelector = "FullyQualifiedName~Fixture.WorkerDispatchTests.ReadyBatch";
    private const string DeclaredClass = "WorkerDispatchPlannerHandoffTests";

    [Fact]
    public void FilenameReturnsReviewer()
    {
        using var fixture = new Scenario();
        var reviewerId = fixture.Reviewer.Id;
        fixture.Submit([Finding("first", InvalidSelector)]);

        fixture.Advance();

        Assert.Equal(GoalStatus.Active, fixture.Goal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Reviewer.Status);
        var returned = Assert.Single(fixture.Retries);
        Assert.Equal(reviewerId, returned.Task);
        Assert.Contains("does not resolve to a class or method in Infrastructure.Tests", returned.Feedback);
        Assert.Contains("Matching test source files declare: ExtraFixtureTests, Fixture.SharedTests, Other.SharedTests, " +
            DeclaredClass, returned.Feedback);
        Assert.Empty(fixture.Escalations);
        Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, Assert.Single(fixture.Rejections));
        var correction = Assert.Single(fixture.Corrections());
        Assert.Equal(reviewerId, correction.Task);
        Assert.Equal("first", correction.Finding);
        Assert.Contains(DeclaredClass, correction.Diagnostic);
    }

    [Fact]
    public void SecondSubmissionFails()
    {
        using var fixture = new Scenario();
        var findings = new[] { Finding("first", InvalidSelector) };
        fixture.Submit(findings);
        var firstSubmission = fixture.Reviewer.LastVerification!;
        var replayedTask = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot())
            .GetTask(fixture.Goal.Id, fixture.Reviewer.Id);
        fixture.Advance();
        Assert.Single(fixture.Corrections());

        fixture.Reload();
        Assert.True(fixture.Driver.TryBuildFindingEvidenceRequest(fixture.Goal, replayedTask,
            ConductorAutonomyPolicy.Permissive, out var replay));
        Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, replay.Kind);
        Assert.Null(replay.TargetTaskId);
        Assert.Single(fixture.Rejections);
        Assert.Single(fixture.Corrections());

        fixture.Submit(findings);
        Assert.NotEqual(firstSubmission.CompletedAt, fixture.Reviewer.LastVerification!.CompletedAt);
        var result = fixture.Advance();

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(fixture.Goal));
        Assert.Equal(WorkTaskStatus.Failed, fixture.Reviewer.Status);
        Assert.Contains("unavailable required evidence", Assert.Single(fixture.Escalations));
        Assert.Single(fixture.Retries);
        Assert.Single(fixture.Corrections());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherRejectionFails(bool mixedBatch)
    {
        using var fixture = new Scenario();
        var other = Finding("other", "FullyQualifiedName~OtherTests", "Core.Tests");
        fixture.Submit(mixedBatch ? [other, Finding("selector", InvalidSelector)] : [other]);
        if (mixedBatch)
        {
            Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
            Assert.Empty(fixture.Retries);
            fixture.Reload();
        }

        var result = fixture.Advance();

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(fixture.Goal));
        Assert.Equal(WorkTaskStatus.Failed, fixture.Reviewer.Status);
        Assert.Contains("unavailable required evidence", Assert.Single(fixture.Escalations));
        Assert.Empty(fixture.Retries);
        Assert.Empty(fixture.Corrections());
        Assert.Contains(FocusedEvidenceRejectionCode.UnsupportedToken, fixture.Rejections);
        Assert.Equal(mixedBatch, fixture.Rejections.Contains(FocusedEvidenceRejectionCode.UnresolvableSelection));
        var findings = fixture.Reviewer.LastVerification!.MergedReviewFindings!;
        Assert.Equal(mixedBatch ? 2 : 1, findings.Count);
        Assert.All(findings, item =>
            Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, item.EvidenceOutcome!.Reason));

        using var selectorOnly = new Scenario();
        selectorOnly.Submit([Finding("selector", InvalidSelector)]);
        selectorOnly.Advance();
        Assert.Equal(selectorOnly.Reviewer.Id, Assert.Single(selectorOnly.Retries).Task);
    }

    private static ReviewFinding Finding(string id, string selector, string project = "Infrastructure.Tests") =>
        new(id, ReviewFindingState.Open, new("tests/Guard.cs", id), "The required test evidence is missing.",
            FindingSeverity.Blocking, FindingCategory.TestEvidence, new FindingEvidenceRequest([new(project, selector)]));

    private sealed class Scenario : IDisposable
    {
        private const string Candidate = "df1cbfc3a1535e3a062ab7fdda0b114c7653c716";
        private static readonly CandidateIdentity Identity = new("fixture-patch", "fixture-base", "fixture-manifest");
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly string _worktree;
        private DateTimeOffset _submissionTime = DateTimeOffset.UtcNow;
        private int _tick;

        internal Scenario()
        {
            (Kernel, var goal) = ReviewGoalWithoutTester();
            var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            PassVerification(Kernel, goal, developer, hasCommittedChanges: true);
            Kernel.RecordDispatchResultCommit(goal.Id, developer.Id, Candidate);
            _worktree = GoalWorktrees.WorktreePath(_root, goal.Id);
            var testDirectory = Path.Combine(_worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(Path.Combine(testDirectory, "nested"));
            File.WriteAllText(Path.Combine(_worktree, ".git"), "gitdir: fixture");
            File.WriteAllText(Path.Combine(testDirectory, "WorkerDispatchTests.cs"),
                "namespace Fixture; public class WorkerDispatchPlannerHandoffTests { [Fact] public void ReadyBatch() {} } " +
                "public class ExtraFixtureTests {} public class SharedTests {}");
            File.WriteAllText(Path.Combine(testDirectory, "nested", "WorkerDispatchTests.cs"),
                "namespace Other; public class SharedTests {}");
            Driver = CreateDriver();
        }

        internal AgentOrchestratorKernel Kernel { get; private set; }
        internal Goal Goal => Kernel.Goals.Single();
        internal TaskSpec Reviewer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        internal ConductorDriver Driver { get; private set; }
        internal List<(TaskId Task, string Feedback)> Retries { get; } = [];
        internal List<string> Escalations { get; } = [];
        internal List<FocusedEvidenceRejectionCode> Rejections { get; } = [];

        internal void Submit(IReadOnlyList<ReviewFinding> findings)
        {
            _submissionTime = _submissionTime.AddMinutes(1);
            var reviewer = Reviewer;
            Kernel.RecordTaskDispatch(Goal.Id, reviewer.Id, new("fixture-reviewer", "review", _worktree, _submissionTime,
                BaseCommit: Candidate, CandidateIdentity: Identity,
                FindingEvidenceReviewContext: FindingEvidenceReviewContext.Capture(Goal, reviewer, Candidate, _submissionTime)));
            var output = string.Join(Environment.NewLine,
                "WORKER_RESULT:", "files: none", "commands: inspect", "tests: deferred - acceptance", "commit: none",
                "blockers: none", $"findings: {JsonSerializer.Serialize(findings)}", "touched_anchors: []", "verdict: needs-work",
                "model_fit: fixture/model - adequate - review", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, reviewer.Id, new("review", _worktree, 0, output, "", _submissionTime.AddSeconds(1),
                WorkerResultPresent: true, ReviewedCommit: Candidate, CandidateIdentity: Identity, DispatchStartedAt: _submissionTime));
            Assert.True(ConductorDriver.UsesQualifiedReviewerContinuation(Goal, Reviewer));
            Assert.Equal(WorkTaskStatus.Failed, Reviewer.Status);
        }

        internal ConductorAdvanceResult Advance()
        {
            Driver.BeginTick(Kernel, ++_tick);
            return Driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
        }

        internal void Reload()
        {
            var snapshot = JsonSerializer.Serialize(Kernel.ExportSnapshot());
            Kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(snapshot)!);
            Driver = CreateDriver();
        }

        internal IEnumerable<(TaskId? Task, string? Finding, string? Diagnostic)> Corrections()
        {
            foreach (var item in Goal.Timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                         item.Message.StartsWith("finding-evidence batch-closed;", StringComparison.Ordinal)))
            {
                using var summary = JsonDocument.Parse(item.Message[item.Message.IndexOf('{')..]);
                if (!summary.RootElement.TryGetProperty("SelectorCorrection", out var correction)) continue;
                yield return (item.TaskId, correction.GetProperty("Findings")[0].GetProperty("FindingId").GetString(),
                    correction.GetProperty("Failures")[0].GetProperty("Diagnostic").GetString());
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
                FocusedEvidenceRejection rejection;
                if (request.StartsWith("Core.Tests:", StringComparison.Ordinal))
                    rejection = new(FocusedEvidenceRejectionCode.UnsupportedToken, "OtherTests", "unsupported token in fixture");
                else
                    Assert.False(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(request, new(), _worktree, out _, out _, out rejection));
                Rejections.Add(rejection.Code);
                return new(request, false, false, rejection.Detail, [], Rejection: rejection);
            },
            retryTaskWithCause: (goal, task, feedback, kind, cause) =>
            {
                Retries.Add((task, feedback));
                return Kernel.RetryTaskAutomatically(goal, task, feedback, retryRoundKind: kind, retryCause: cause);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Deferred("Reviewer is ready for correction."),
            startRecordedDispatches: _ => throw new InvalidOperationException("Fixture must never launch a worker."),
            writeEscalation: (_, _, reason) => Escalations.Add(reason),
            recordFindingEvidenceRequest: (goal, task, message) => Kernel.RecordFindingEvidenceRequest(goal, task, message),
            recordFindingEvidenceRun: (goal, task, message) => Kernel.RecordFindingEvidenceRun(goal, task, message),
            recordFindingEvidenceRequestDispositions: (goal, task, dispositions) => Kernel.RecordFindingEvidenceRequestDispositions(goal, task, dispositions),
            recordFindingEvidenceOutcome: (goal, task, id, outcome, receipt) => Kernel.RecordFindingEvidenceOutcome(goal, task, id, outcome, receipt));

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
