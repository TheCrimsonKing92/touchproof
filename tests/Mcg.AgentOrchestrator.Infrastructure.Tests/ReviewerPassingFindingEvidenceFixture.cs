using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: in-memory kernel, unique artifact directory, injected execution/dispatch seams.
internal sealed class ReviewerPassingFindingEvidenceFixture : IDisposable
{
    private readonly string _root = ConductorDriverTests.CreateTempDirectory();
    private ConductorDriver _driver = null!;
    private int _tick;
    internal AgentOrchestratorKernel Kernel { get; private set; }
    internal Goal Goal => Kernel.Goals.Single();
    internal TaskSpec Reviewer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
    internal TaskSpec Developer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
    internal string Candidate { get; set; }
    internal List<string> Executions { get; } = [];
    internal List<(TaskId Task, RetryCause Cause, string Feedback)> Retries { get; } = [];
    internal int ReviewDispatches => Reviewer.DispatchHistory.Count;
    internal IReadOnlyList<FindingEvidenceReceipt> Receipts => Reviewer.VerificationHistory
        .SelectMany(record => record.FindingEvidenceReceipts ?? []).DistinctBy(receipt => receipt.ReceiptId).ToArray();

    internal ReviewerPassingFindingEvidenceFixture(string candidate = "df1cbfc3a1535e3a062ab7fdda0b114c7653c716")
    {
        Candidate = candidate;
        var created = SoftwareGoal();
        Kernel = created.Kernel;
        foreach (var task in Goal.Tasks.TakeWhile(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(Kernel, Goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
            if (task.RequiredRole == AgentRole.Developer)
                Kernel.RecordDispatchResultCommit(Goal.Id, task.Id, Candidate);
        }
        RestartDriver();
    }

    internal void Report(IReadOnlyList<ReviewFinding> findings, bool legacyContext = false,
        bool completed = true, bool interrupted = false)
    {
        var at = DateTimeOffset.UtcNow;
        Kernel.RecordTaskDispatch(Goal.Id, Reviewer.Id, new TaskDispatchRecord("fixture-reviewer", "review", _root, at,
            BaseCommit: Candidate, FindingEvidenceReviewContext: legacyContext ? null :
                FindingEvidenceReviewContext.Capture(Goal, Reviewer, Candidate, at),
            FindingEvidenceReviewContinuation: Reviewer.FindingEvidenceReviewContinuation));
        if (!completed) return;
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect delivered evidence",
            "tests: deferred - acceptance owns execution", "commit: none", "blockers: none",
            $"findings: {JsonSerializer.Serialize(findings)}", "touched_anchors: []", "verdict: needs-work",
            "model_fit: fixture/model - adequate - review", "skills: none", "confidence: high", "END_WORKER_RESULT");
        Kernel.RecordDispatchExecutionResult(Goal.Id, Reviewer.Id, new TaskVerificationRecord(
            "review", _root, interrupted ? 1 : 0, interrupted ? "provider interrupted" : output, "",
            DateTimeOffset.UtcNow, WorkerResultPresent: !interrupted, DispatchStartedAt: at, DispatchAttemptAt: at));
    }

    internal ConductorAdvanceResult Advance(ConductorAutonomyPolicy? policy = null)
    {
        _driver.BeginTick(Kernel, ++_tick);
        return _driver.AdvanceOnce(Goal, policy ?? ConductorAutonomyPolicy.Permissive);
    }

    internal ConductorAdvanceResult AdvanceToDecision(ConductorAutonomyPolicy? policy = null)
    {
        var before = Retries.Count;
        var result = Advance(policy);
        for (var step = 0; step < 8 && Retries.Count == before &&
            !(result.Outcome is ConductorAdvanceOutcome.Held held && held.Reason.Contains("REVIEWER_PASSING_EVIDENCE_HOLD", StringComparison.Ordinal)); step++)
            result = Advance(policy);
        return result;
    }

    internal void Reload()
    {
        Kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(Kernel.ExportSnapshot()))!);
        RestartDriver();
    }

    internal void ReattachVerification(TaskVerificationRecord record) => Kernel.RecordTaskVerification(
        Goal.Id, Reviewer.Id, record with { WorkerResultPresent = false, CompletedAt = DateTimeOffset.UtcNow });

    internal void PruneLegacyDeliveryMarkers()
    {
        var snapshot = Kernel.ExportSnapshot();
        Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Timeline = goal.Timeline.Where(item => item.Kind != ProgressKind.TaskRetried ||
                    !item.Message.StartsWith("finding evidence-on-demand:", StringComparison.Ordinal)).ToArray()
            }).ToArray()
        });
        RestartDriver();
    }

    internal bool Decide(out FailedGoalFindingObservation observation, ReviewRetryCapReceipt? cap = null,
        bool includeDeveloper = true) => ReviewerPassingFindingEvidenceRouter.TryDecide(
            Goal, Reviewer, includeDeveloper ? Developer : null, Candidate,
            NormalizeRequest,
            ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(new AcceptanceGateEngineSettings()),
            cap ?? ReviewRetryCapReceipt.Create(Goal, ConductorAutonomyPolicy.Permissive.ReviewAutoRetryStopRound),
            out observation);

    internal static FindingEvidenceRequest? NormalizeRequest(FindingEvidenceRequest request) =>
        ConductorDriver.TryNormalizeFindingEvidenceRequest(request, new AcceptanceGateEngineSettings(),
            (_, testClass) => [testClass], out var normalized, out _, out _, out _) ? normalized : null;

    internal FindingEvidenceReceipt SeedPreTesterCoverage(FindingEvidenceRequest request)
    {
        // Pre-Tester covered a broader guard set, not this finding's own execution request.
        var coverage = request with
        {
            Selections = request.Selections.Append(new FindingEvidenceSelection("Infrastructure.Tests", "ConductorDriverTests")).ToArray()
        };
        var normalized = Assert.IsType<FindingEvidenceRequest>(NormalizeRequest(coverage));
        var selected = string.Join("; ", normalized.Selections.Select(ConductorDriver.FormatFindingEvidenceSelection));
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            _root, TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null);
        // Retain original execution authority before the Reviewer requests it. The driver's
        // execution callback must stay unused when it subsequently reattaches this receipt.
        var decision = coordinator.EvaluateFocusedEvidence(
            ConductorParallelAcceptanceCandidate.Create(Goal, 0, [], Candidate, "main123"),
            ConductorAutonomyPolicy.Permissive, selected,
            (_, selection, _, _) => ConductorDriverTestsFindingEvidenceReuse
                .RetainedEvidenceWithExecutedClasses(_root, selection, Candidate) with
                { OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence },
            new ConductorFocusedEvidenceRequestContext("pre-tester-deferred", "incident-coverage", [],
                ExecutionBasisIdentity: ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(new()),
                ReceiptId: "pre-tester-incident-receipt"));
        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
        var receipt = Assert.IsType<FindingEvidenceReceipt>(coordinator.ReadOriginalFocusedReceipt(decision.Attempt));
        coordinator.MarkReconciled(decision.Attempt);
        return receipt;
    }

    private void RestartDriver() => _driver = MakeDriver(
        getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
        resolveFindingEvidenceSiblingClasses: (_, _, testClass) => [testClass],
        focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
            _root, TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
        runFocusedEvidence: (_, request) =>
        {
            Executions.Add(request);
            var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_root, request, Candidate);
            return green with { Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(),
                OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence };
        },
        retryTaskWithCause: (goal, task, message, kind, cause) =>
        {
            Retries.Add((task, cause, message));
            return Kernel.RetryTask(goal, task, message, retryRoundKind: kind, retryCause: cause);
        },
        recordFindingEvidenceRequest: (goal, task, message) => Kernel.RecordFindingEvidenceRequest(goal, task, message),
        recordFindingEvidenceRun: (goal, task, message) => Kernel.RecordFindingEvidenceRun(goal, task, message),
        recordFindingEvidenceRequestDispositions: (goal, task, dispositions) => Kernel.RecordFindingEvidenceRequestDispositions(goal, task, dispositions),
        recordFindingEvidenceOutcome: (goal, task, id, outcome, receipt) => Kernel.RecordFindingEvidenceOutcome(goal, task, id, outcome, receipt));

    internal static ReviewFinding CliFinding() => new("cli-surface-started-not-run-obligation-loss", ReviewFindingState.Open,
        new("src/Mcg.AgentOrchestrator.App/Orchestration/PreTesterGuardSelections.cs", "34-37,70-76"),
        "On resumption, `PreTesterGuardSelections.Create` skips CLI discovery and `Augment` restores only prior.Selections. " +
        "Preserve the CLI-only class in prior.NotRun; otherwise ConductorDriver.PreTesterDeferredEvidence.cs:120-123 rejects the unchanged attempt.",
        Category: FindingCategory.Correctness,
        EvidenceRequest: new([new("Infrastructure.Tests", "PreTesterCliSurfaceSelectionTests"),
            new("Infrastructure.Tests", "PreTesterCliSurfaceWiringTests"),
            new("Infrastructure.Tests", "ConductorDriverTestsPreTesterAlwaysRunGuards")]));

    internal static ReviewFinding CaptureFinding() => new("junit-environment-capture-limit-reference", ReviewFindingState.Open,
        new("src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/AcceptanceCheckProcessInvoker.cs", "272-278"),
        "`EmitCaptureLimitReached` runs regardless of SuppressDiagnosticCaptures. AcceptanceCaptureDrain.cs:184-189 forwards it to Console.Out " +
        "and GoalAcceptanceVerifier.CaptureCustody.cs:251-252 publishes the raw path before cleanup at AcceptanceCheckProcessInvoker.cs:440-443. " +
        "Suppress the capture-limit path when diagnostic captures are suppressed.", Category: FindingCategory.Correctness,
        EvidenceRequest: new([new("Infrastructure.Tests", "JunitCommandEnvironmentDiagnosticTests")]));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
