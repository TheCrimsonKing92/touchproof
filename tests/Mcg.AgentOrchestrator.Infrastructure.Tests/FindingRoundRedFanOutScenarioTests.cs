using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: each scenario owns its kernel and temporary attempt directory; runners and dispatch are fakes.
public sealed class FindingRoundRedFanOutScenarioTests
{
    private const int CrossTickHangFailsafeMilliseconds = 30_000;
    private const string Candidate = "abc1234";
    private const string AlphaFailure = "Fixture.AlphaTests.FailsOnCandidate";
    private const string BetaFailure = "Fixture.BetaTests.FailsOnCandidate(outcome: Interrupted)";
    private const string UnionRequest = "Infrastructure.Tests:AlphaTests; Infrastructure.Tests:BetaTests";
    private const string CoreRequest = "Core.Tests:FullyQualifiedName~GammaMethod";

    [Fact]
    public void PrefixedTwoMemberRedRoutesDeveloperAfterOneRun()
    {
        using var scenario = new Scenario([AlphaFailure, BetaFailure]);
        var identity = scenario.BatchIdentity;

        var result = scenario.Advance();

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(new[] { UnionRequest }, scenario.Runs);
        var retry = Assert.Single(scenario.Retries);
        Assert.Equal(scenario.Developer.Id, retry.Task);
        Assert.StartsWith("ACTIONABLE_CANDIDATE_RED", retry.Message, StringComparison.Ordinal);
        Assert.Contains(AlphaFailure, retry.Message, StringComparison.Ordinal);
        Assert.Contains(BetaFailure, retry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CoreRequest, scenario.Runs);
        Assert.Contains(scenario.Goal.Timeline, item => item.TaskId == scenario.Reviewer.Id &&
            item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            item.Message.Contains("finding_id=gamma;", StringComparison.Ordinal) &&
            item.Message.Contains("reason=superseded-by-actionable-red;", StringComparison.Ordinal));
        Assert.Equal(1, scenario.Dispatches);
        Assert.Empty(scenario.Escalations);
        var closing = Assert.Single(scenario.Goal.Timeline.Where(item =>
            item.Message.StartsWith($"finding-evidence batch-closed; batch_key={identity.Key};", StringComparison.Ordinal)));
        var summary = JsonSerializer.Deserialize<FindingRoundBatchAccounting.Summary>(
            closing.Message[closing.Message.IndexOf('{')..])!;
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(2, summary.NewlyExecuted);
        Assert.Equal(1, summary.Superseded);
        Assert.Equal(0, summary.Covered);
        Assert.Equal(0, summary.Unavailable);
        Assert.True(summary.IsSettled);
        Assert.Equal(0, summary.PaidVerificationCallsAvoided);
        Assert.Contains("Developer", summary.RoutingReason, StringComparison.Ordinal);
        var restored = scenario.RestoredGoal();
        Assert.False(FindingEvidenceBatchClosureAdmission.TryApply(restored, new(identity, "replayed"), () => true,
            () => throw new InvalidOperationException("Actionable red retried twice after restoration.")));
    }

    [Fact]
    public void UnattributableRedRunsOnceThenNextDistinctRequest()
    {
        using var scenario = new Scenario(["Fixture.SharedSetupTests.SetupFails"]);

        var first = scenario.Advance();
        var pending = Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome);
        Assert.Contains($"next_request={scenario.CoreIdentity}", pending.Reason, StringComparison.Ordinal);
        Assert.Contains("completed_receipt_id=finding-evidence-", pending.Reason, StringComparison.Ordinal);
        // Restarting loses driver memory: the stored RED receipt must advance the round.
        scenario.RestartDriver();
        for (var tick = 1; tick < 6 && scenario.Retries.Count == 0; tick++) scenario.Advance();

        Assert.Equal(new[] { UnionRequest, CoreRequest }, scenario.Runs);
        Assert.Single(scenario.Runs.Where(request => request == UnionRequest));
        Assert.Single(scenario.Runs.Where(request => request == CoreRequest));
        Assert.DoesNotContain(scenario.Retries, retry =>
            retry.Message.StartsWith("ACTIONABLE_CANDIDATE_RED", StringComparison.Ordinal));
        var retry = Assert.Single(scenario.Retries);
        Assert.Equal(scenario.Reviewer.Id, retry.Task);
        var receipts = scenario.Reviewer.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? []).ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.All(receipts, receipt => Assert.Contains(receipt.ReceiptId, retry.Message, StringComparison.Ordinal));
        Assert.Contains(scenario.UnionIdentity, retry.Message, StringComparison.Ordinal);
        Assert.Empty(scenario.Escalations);
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void InheritedBaselineRed_DrainsSiblingAfterRestartBeforeOneRecovery()
    {
        using var scenario = new Scenario(["Fixture.SharedSetupTests.SetupFails"], inheritBaseline: true);
        var identity = scenario.BatchIdentity;
        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        Assert.Equal(new[] { UnionRequest }, scenario.Runs);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Escalations);

        scenario.RestartDriver();
        scenario.Advance();
        scenario.Advance();

        Assert.Equal(new[] { UnionRequest, CoreRequest }, scenario.Runs);
        Assert.Contains("inherited from a RED Baseline arm", Assert.Single(scenario.Escalations), StringComparison.Ordinal);
        Assert.Empty(scenario.Retries);
        Assert.Equal(0, scenario.Dispatches);
        Assert.True(FindingRoundBatchAccounting.IsClosed(scenario.RestoredGoal(), identity));
    }

    [Fact]
    public void ExhaustedRequestAllowsLaterRequestBeforeUnavailableRecovery()
    {
        using var scenario = new Scenario([], armless: true);

        for (var tick = 0; tick < 8; tick++) scenario.Advance();

        Assert.Equal(Enumerable.Repeat("Infrastructure.Tests:FullyQualifiedName~AlphaTests", 3).Concat(Enumerable.Repeat(CoreRequest, 3)), scenario.Runs);
        var escalation = Assert.Single(scenario.Escalations);
        Assert.Contains("unavailable", escalation, StringComparison.Ordinal);
        Assert.Contains("No worker was dispatched.", escalation, StringComparison.Ordinal);
        Assert.Equal(6, scenario.Goal.Timeline.Count(item => item.Kind == ProgressKind.FindingEvidenceRunRecorded));
        Assert.Empty(scenario.Retries);
        Assert.Equal(0, scenario.Dispatches);
    }

    private sealed class Scenario : IDisposable
    {
        private readonly AgentOrchestratorKernel _kernel;
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly string[] _failures;
        private readonly bool _armless;
        private readonly bool _inheritBaseline;
        private ConductorDriver _driver = null!;

        internal Scenario(string[] failures, bool armless = false, bool inheritBaseline = false)
        {
            _failures = failures;
            _armless = armless;
            _inheritBaseline = inheritBaseline;
            (_kernel, Goal) = SoftwareGoal();
            Developer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in Goal.Tasks.Where(task => task != Reviewer))
                PassVerification(_kernel, Goal, task, hasCommittedChanges: task == Developer);
            var findings = new List<ReviewFinding>
            {
                EvidenceFindingWithRequest("Alpha needs evidence", "alpha", classes: ["FullyQualifiedName~AlphaTests"])
            };
            if (!armless) findings.Add(EvidenceFindingWithRequest("Beta needs evidence", "beta",
                classes: ["FullyQualifiedName~BetaTests"]));
            findings.Add(EvidenceFindingWithRequest("Gamma spec needs evidence", "gamma",
                category: FindingCategory.SpecDefect, project: "Core.Tests", classes: ["Name~GammaMethod"]));
            FailReviewerNeedsWork(_kernel, Goal, Reviewer, "Distinct focused requests need evidence", findings: findings);
            FirstIdentity = ConductorDriver.BuildFindingEvidenceIdentity(findings[0].EvidenceRequest!);
            CoreIdentity = ConductorDriver.BuildFindingEvidenceIdentity(new([new("Core.Tests", "FullyQualifiedName~GammaMethod")]));
            UnionIdentity = ConductorDriver.BuildFindingEvidenceIdentity(new(
                [new("Infrastructure.Tests", "AlphaTests"), new("Infrastructure.Tests", "BetaTests")]));
            RestartDriver();
        }

        internal Goal Goal { get; }
        internal TaskSpec Developer { get; }
        internal TaskSpec Reviewer { get; }
        internal string FirstIdentity { get; }
        internal string CoreIdentity { get; }
        internal string UnionIdentity { get; }
        internal List<string> Runs { get; } = [];
        internal List<(TaskId Task, string Message)> Retries { get; } = [];
        internal List<string> Escalations { get; } = [];
        internal int Dispatches { get; private set; }
        internal FindingEvidenceBatchIdentity BatchIdentity => FindingRoundBatchCoordinator.Identity(_driver, Goal, Reviewer)!;
        internal Goal RestoredGoal() => AgentOrchestratorKernel.FromSnapshot(_kernel.ExportSnapshot()).GetGoal(Goal.Id);

        internal ConductorAdvanceResult Advance() => _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);

        internal void RestartDriver() => _driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
            focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                _root, TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null),
            runFocusedEvidence: (_, request) =>
            {
                Runs.Add(request);
                if (_armless) return new FocusedEvidenceRunResult(request, true, false, "Focused failure without arms",
                    [new AcceptanceCheckResult("candidate", false, 1, "Executed: 1", ExecutedTestCount: 1)],
                    Arms: [], OutcomeReason: FindingEvidenceOutcomeReason.ValidEvidence);
                var red = CandidateRedFindingEvidence(request, Candidate, _failures[0]);
                var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
                var check = candidate.Checks.Single() with { FailingTestIdentities = _failures, ExecutedTestCount = 2 };
                var inherited = _inheritBaseline && request == UnionRequest;
                return red with
                {
                    Checks = [check],
                    Arms = red.Arms!.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
                        ? arm with { Checks = [check] }
                        : inherited ? arm with { Checks = [check], Passed = false, Disposition = FindingEvidenceArmDisposition.Red }
                        : arm).ToArray()
                };
            },
            dispatchAndStart: _ => { Dispatches++; return DispatchStartOutcome.Started(); },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                Retries.Add((taskId, message));
                return _kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => Escalations.Add(message),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                _kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                _kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceRequestDispositions: (goalId, taskId, dispositions) =>
                _kernel.RecordFindingEvidenceRequestDispositions(goalId, taskId, dispositions),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                _kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
