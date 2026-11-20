using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsPreReviewTesterInsertion
{
    private const string MappingNeedsInput = "mapping needs input";
    private const string RejectedRequest = "rejected request";
    private const string CoverageMismatch = "coverage mismatch";
    private const string ApparatusFailure = "apparatus failure";
    private const string MappingEscalation = "PRE_REVIEW_MAPPING_NEEDS_INPUT";
    private const string ApparatusEscalation = "PRE_REVIEW_SELECTION_APPARATUS_FAILURE";

    [Theory]
    [InlineData(MappingNeedsInput, "pre-review mapping requires Tester selection")]
    [InlineData(RejectedRequest, "pre-review focused-evidence request was rejected")]
    [InlineData(CoverageMismatch, "pre-review evidence mapping failure")]
    [InlineData(ApparatusFailure, "pre-review focused selection apparatus failure")]
    public void TwoRoleGoal_InsertsAndDispatchesTester(string routingOutcome, string reasonPrefix)
    {
        var scenario = new RoutingScenario(routingOutcome);
        Assert.Equal([AgentRole.Developer, AgentRole.Reviewer], scenario.Goal.Tasks.Select(task => task.RequiredRole));

        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);

        Assert.Equal([AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            scenario.Goal.Tasks.Select(task => task.RequiredRole));
        var tester = AssignedAndDispatchedTester(scenario);
        Assert.StartsWith($"{reasonPrefix} for candidate {scenario.CandidateSha}", tester.Description, StringComparison.Ordinal);
        Assert.Equal(Assert.Single(scenario.InsertionReasons), tester.Description);
        Assert.Equal(WorkTaskStatus.Completed, scenario.Developer.Status);
        Assert.Null(scenario.Reviewer.LastDispatch);
        Assert.Empty(scenario.Retried);
        Assert.Empty(scenario.Escalations);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "CrossTick")]
    public void LaterRouting_RetriesTheSameTester(bool hasExistingTester)
    {
        var scenario = new RoutingScenario(MappingNeedsInput);
        TaskSpec? existingTester = null;
        if (hasExistingTester)
        {
            existingTester = PreReviewTesterInsertion.Add(
                scenario.Kernel, DefaultAgents(), scenario.Goal.Id, "Existing Tester");
            PassVerification(scenario.Kernel, scenario.Goal, existingTester);
        }

        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);

        var tester = AssignedAndDispatchedTester(scenario);
        if (existingTester is not null)
        {
            Assert.Equal(existingTester.Id, tester.Id);
            Assert.Equal([(tester.Id, (RetryRoundKind?)RetryRoundKind.Mechanical, RetryCause.NewTestFinding)], scenario.Retried);
        }
        else
        {
            Assert.Empty(scenario.Retried);
        }

        scenario.Kernel.RecordTaskVerification(scenario.Goal.Id, tester.Id, new TaskVerificationRecord(
            "test.exe", @"C:\tmp", 0, "ok", "", DateTimeOffset.UtcNow));
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        scenario.CandidateSha = "second-candidate";
        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);

        Assert.Equal(tester.Id, Assert.Single(scenario.Goal.Tasks.Where(task => task.RequiredRole == AgentRole.Tester)).Id);
        Assert.Equal(new[] { (tester.Id, WorkTaskStatus.Assigned), (tester.Id, WorkTaskStatus.Assigned) }, scenario.Dispatched);
        Assert.Equal(hasExistingTester ? 2 : 1, scenario.Retried.Count);
        Assert.All(scenario.Retried, retry =>
            Assert.Equal((tester.Id, (RetryRoundKind?)RetryRoundKind.Mechanical, RetryCause.NewTestFinding), retry));
        Assert.Equal(hasExistingTester ? 0 : 1, scenario.InsertionReasons.Count);
        Assert.Empty(scenario.Escalations);
    }

    [Theory]
    [InlineData(MappingNeedsInput, MappingEscalation)]
    [InlineData(ApparatusFailure, ApparatusEscalation)]
    public void InsertionThrows_EscalatesWithoutDispatchOrTaskFailure(string routingOutcome, string category)
    {
        var scenario = new RoutingScenario(routingOutcome);
        scenario.Driver.AddPreReviewTester = (_, _) => throw new InvalidOperationException("injected insertion error");

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Advance().Outcome);

        var escalation = Assert.Single(scenario.Escalations);
        Assert.StartsWith(category + ":", escalation, StringComparison.Ordinal);
        Assert.Contains("Tester insertion failed: InvalidOperationException: injected insertion error", escalation, StringComparison.Ordinal);
        AssertInsertionHadNoTaskSideEffects(scenario);
    }

    [Fact]
    public void NoAvailableTesterAgent_EscalatesWithoutLeavingAPendingTask()
    {
        var scenario = new RoutingScenario(MappingNeedsInput);
        scenario.Driver.AddPreReviewTester = (goalId, reason) =>
            PreReviewTesterInsertion.Add(scenario.Kernel, [], goalId, reason);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Advance().Outcome);

        Assert.Contains("No available Tester agent can be assigned.", Assert.Single(scenario.Escalations), StringComparison.Ordinal);
        AssertInsertionHadNoTaskSideEffects(scenario);
    }

    private static TaskSpec AssignedAndDispatchedTester(RoutingScenario scenario)
    {
        var tester = Assert.Single(scenario.Goal.Tasks.Where(task => task.RequiredRole == AgentRole.Tester));
        Assert.Equal((tester.Id, WorkTaskStatus.Assigned), Assert.Single(scenario.Dispatched));
        Assert.NotNull(tester.LastDispatch);
        Assert.Equal(WorkTaskStatus.Running, tester.Status);
        return tester;
    }

    private static void AssertInsertionHadNoTaskSideEffects(RoutingScenario scenario)
    {
        Assert.Equal([scenario.Developer.Id, scenario.Reviewer.Id], scenario.Goal.Tasks.Select(task => task.Id));
        Assert.Equal([WorkTaskStatus.Completed, WorkTaskStatus.Assigned], scenario.Goal.Tasks.Select(task => task.Status));
        Assert.Empty(scenario.Dispatched);
        Assert.Empty(scenario.Retried);
    }

    private sealed class RoutingScenario
    {
        private readonly ConductorAutonomyPolicy _policy;

        internal RoutingScenario(string routingOutcome)
        {
            _policy = routingOutcome == CoverageMismatch
                ? ConductorAutonomyPolicy.Conservative
                : ConductorAutonomyPolicy.Permissive;
            (Kernel, Goal) = ReviewGoalWithoutTester();
            Developer = Goal.Tasks[0];
            Reviewer = Goal.Tasks[1];
            PassVerification(Kernel, Goal, Developer);
            var agents = DefaultAgents();
            Driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => routingOutcome == MappingNeedsInput
                    ? new PreReviewEvidenceContext(CandidateSha, [], null, "unmapped test impact",
                        NoApplicableTests: false, MappingNeedsInput: true)
                    : FocusedPreReviewContext(CandidateSha),
                runFocusedEvidence: (_, request) => routingOutcome switch
                {
                    RejectedRequest => new(request, Accepted: false, Passed: false, "rejected by broker", []),
                    CoverageMismatch => new(request, Accepted: true, Passed: true, "no mapped checks", []),
                    ApparatusFailure => new(request, Accepted: true, Passed: false, "executed zero tests",
                        [new AcceptanceCheckResult("focused selection", false, 8, "executed zero tests",
                            FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
                            ExecutedTestCount: 0)],
                        OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure),
                    _ => throw new InvalidOperationException($"Unexpected focused-evidence request for {routingOutcome}.")
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) => Kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                retryTaskWithCause: (goalId, taskId, reason, round, cause) =>
                {
                    Retried.Add((taskId, round, cause));
                    return Kernel.RetryTaskAutomatically(goalId, taskId, reason, retryRoundKind: round, retryCause: cause);
                },
                dispatchAndStart: goal =>
                {
                    var selected = DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(
                        goal, agents, approveHighRiskOwnership: _policy.AllowsAutonomousHighRiskOwnership);
                    var task = goal.Tasks.Single(task => selected.TaskIds.Contains(task.Id));
                    Dispatched.Add((task.Id, task.Status));
                    DispatchTask(Kernel, goal, task);
                    return DispatchStartOutcome.Started([task]);
                },
                writeEscalation: (_, _, reason) => Escalations.Add(reason));
            Driver.AddPreReviewTester = (goalId, reason) =>
            {
                InsertionReasons.Add(reason);
                return PreReviewTesterInsertion.Add(Kernel, agents, goalId, reason);
            };
        }

        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal TaskSpec Developer { get; }
        internal TaskSpec Reviewer { get; }
        internal ConductorDriver Driver { get; }
        internal string CandidateSha { get; set; } = "first-candidate";
        internal List<string> InsertionReasons { get; } = [];
        internal List<(TaskId, WorkTaskStatus)> Dispatched { get; } = [];
        internal List<(TaskId, RetryRoundKind?, RetryCause)> Retried { get; } = [];
        internal List<string> Escalations { get; } = [];

        internal ConductorAdvanceResult Advance() => Driver.AdvanceOnce(Goal, _policy);
    }
}
