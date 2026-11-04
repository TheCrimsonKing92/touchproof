using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: private SQLite paths and in-memory conductor delegates; no real processes.
public sealed class PlannerAcceptanceOwnershipLifecycleTests
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Request = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"execution","availability":"post-implementation","owner":"acceptance-gate","needed":"Run candidate checks","reason":"Acceptance owns execution","tests":["PlannerAcceptanceOwnershipLifecycleTests"]}
        """;
    private const string Result = "WORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: deferred - PlannerAcceptanceOwnershipLifecycleTests\ncommit: none\nblockers: none\nmodel_fit: fixture/model - adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";

    [Fact]
    public async Task AcceptedOwnershipAndDeferredReviewSurviveSqliteReloadWithoutExecutionReceipt()
    {
        using var f = new Fixture();
        f.Plan();
        var before = JsonSerializer.Serialize(f.Goal.CriterionEvidenceObligations);
        await f.Reload();
        Assert.Equal(before, JsonSerializer.Serialize(f.Goal.CriterionEvidenceObligations));
        f.Review();
        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        Assert.Equal(GoalStatus.Verified, f.Goal.Status);
        await f.Reload();
        var pending = Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate));
        Assert.Equal(CriterionEvidenceOwner.Acceptance, pending.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, pending.State);
        Assert.Equal(Candidate, pending.ExpectedCandidateSha);
        Assert.Null(pending.ReceiptId);
        var source = Assert.Single(pending.PlannerSources!);
        Assert.Equal(f.Goal.Id.Value, source.Identity.GoalId);
        Assert.Equal(f.Planner.Id.Value, source.Identity.RequestingTaskId);
        Assert.Equal(f.Goal.AuthoritativeBrief.Version, source.Identity.BriefVersion);
        Assert.Equal(1, source.Identity.CriterionIndex);
        Assert.Equal("execution", source.Identity.EvidenceKey);
        Assert.Equal("Run candidate checks", source.Directive.Needed);
        Assert.Throws<InvalidOperationException>(() => f.Kernel.CompleteGoal(f.Goal.Id, "source review alone"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameBriefReplacementWithoutAcceptedDirectiveBlocksReviewAfterReload(bool failed)
    {
        using var f = new Fixture();
        f.Plan();
        var history = JsonSerializer.Serialize(Assert.Single(f.Goal.CriterionEvidenceObligations).PlannerSources);
        f.RetryPlan();
        f.Plan(failed ? Request : "No execution directive.", failed ? 1 : 0);
        await f.Reload();
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "do not revive historical Planner authority");
        f.Review();
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        Assert.Equal(history, JsonSerializer.Serialize(Assert.Single(f.Goal.CriterionEvidenceObligations).PlannerSources));
        await f.Reload();
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "do not repair review from a superseded Planner round");
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingOperatorMappingSurvivesBriefRevisionAndSqliteReload(bool? passed)
    {
        using var f = new Fixture();
        f.Plan();
        f.Review();
        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        var before = Assert.Single(f.Goal.CriterionEvidenceObligations);
        if (passed is { } result)
            f.Kernel.RecordCriterionEvidence(f.Goal.Id, before.Id, before.Owner, Candidate,
                "actual-full-gate", before.RequiredScope, result, "Candidate execution result");
        before = Assert.Single(f.Goal.CriterionEvidenceObligations);
        var mapped = f.Kernel.MapCriterionEvidenceOwner(f.Goal.Id, 0, 1, before.Owner, "operator",
            before.RequiredScope, before.FindingStableId, before.ExpectedCandidateSha);
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "Revised brief preserves criteria and explicit execution ownership.");
        await f.Reload();
        var restored = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Equal("operator mapping by operator", restored.Provenance);
        Assert.False(Assert.Single(restored.PlannerSources!).EstablishesOwnership);
        Assert.Equal(JsonSerializer.Serialize(mapped), JsonSerializer.Serialize(restored));
        Assert.Equal(before.State, restored.State);
        Assert.Equal(before.ReceiptId, restored.ReceiptId);
        Assert.Equal(before.CandidateSha, restored.CandidateSha);
        Assert.Equal(before.Detail, restored.Detail);
        f.Kernel.RetryTask(f.Goal.Id, f.Reviewer.Id, "Review the revised brief under explicit ownership.");
        f.Review();
        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        Assert.Equal(before.State, Assert.Single(f.Goal.CriterionEvidenceObligations).State);
    }

    [Fact]
    public async Task AcceptedHistoricalBatchReconcilesAtomicallyInGoalTransaction()
    {
        using var f = new Fixture();
        f.Plan();
        var snapshot = f.Kernel.ExportSnapshot();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = [snapshot.Goals[0] with { CriterionEvidenceObligations = [] }] });
        f.Review();
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        var reviews = JsonSerializer.Serialize(f.Reviewer.VerificationHistory);
        var plans = JsonSerializer.Serialize(f.Planner.VerificationHistory);
        await f.Repository.SaveAsync(f.Kernel);
        var changed = await f.Repository.TransactGoalStateAsync(f.Goal.Id, (stored, _) =>
        {
            Assert.NotNull(stored);
            var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored.Goal], stored.HumanInputRequests));
            var didChange = kernel.NormalizeGoalLifecycleState(f.Goal.Id, "reconcile accepted Planner ownership");
            var current = kernel.ExportSnapshot();
            return Task.FromResult((didChange, (GoalStateSnapshot?)new GoalStateSnapshot(current.Goals.Single(), current.HumanInputRequests), didChange));
        });
        Assert.True(changed);
        f.Kernel = await f.Repository.LoadAsync();
        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        Assert.Equal(reviews, JsonSerializer.Serialize(f.Reviewer.VerificationHistory));
        Assert.Equal(plans, JsonSerializer.Serialize(f.Planner.VerificationHistory));
        Assert.Equal(CriterionEvidenceState.Pending, Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate)).State);
        Assert.False(f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "idempotent replay"));
    }

    [Fact]
    public async Task ConcurrentExplicitAuthorityRejectsEntireProjectionAndReviewStatusDelta()
    {
        using var f = new Fixture();
        f.Plan();
        var snapshot = f.Kernel.ExportSnapshot();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = [snapshot.Goals[0] with { CriterionEvidenceObligations = [] }] });
        f.Review();
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.True(f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "stale tick projects ownership"));
        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        var tick = f.Kernel.ExportSnapshot();
        await f.Repository.TransactGoalStateAsync(f.Goal.Id, (stored, _) =>
        {
            var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!.Goal], stored.HumanInputRequests));
            kernel.MapCriterionEvidenceOwner(f.Goal.Id, 0, 1, CriterionEvidenceOwner.Operator,
                "operator", "manual proof", expectedCandidateSha: Candidate);
            var saved = kernel.ExportSnapshot();
            return Task.FromResult((true, (GoalStateSnapshot?)new GoalStateSnapshot(saved.Goals.Single(), saved.HumanInputRequests), true));
        });
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline, tick.Goals.Single(), tick.HumanInputRequests)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Skipped, result.Disposition);
        Assert.Contains("Planner ownership projection conflicts", result.Message);
        f.Kernel = await f.Repository.LoadAsync();
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        var authority = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceOwner.Operator, authority.Owner);
        Assert.Null(authority.PlannerSources);
        Assert.Throws<InvalidOperationException>(() => f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "fresh conflict"));
        Assert.Equal(authority, Assert.Single(f.Goal.CriterionEvidenceObligations));
    }

    [Theory]
    [InlineData(GoalStatus.Cancelled, false)]
    [InlineData(GoalStatus.Cancelled, true)]
    [InlineData(GoalStatus.Superseded, false)]
    [InlineData(GoalStatus.Superseded, true)]
    public async Task ConcurrentStopRejectsEntireOwnershipReconciliation(
        GoalStatus terminalStatus, bool ownershipRecorded)
    {
        using var f = new Fixture();
        f.Plan();
        var snapshot = f.Kernel.ExportSnapshot();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = [snapshot.Goals[0] with { CriterionEvidenceObligations = [] }] });
        f.Review();
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        if (ownershipRecorded)
        {
            f.Plan();
            Assert.NotEmpty(Assert.Single(f.Goal.CriterionEvidenceObligations).PlannerSources!);
            Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        }
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.Null(baseline.ExplicitDisposition);
        Assert.True(f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "stale ownership reconciliation"));
        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        Assert.Equal(GoalStatus.Verified, f.Goal.Status);
        Assert.NotEmpty(Assert.Single(f.Goal.CriterionEvidenceObligations).PlannerSources!);
        var tick = f.Kernel.ExportSnapshot();
        if (ownershipRecorded)
            Assert.Equal(JsonSerializer.Serialize(Assert.Single(baseline.CriterionEvidenceObligations!).PlannerSources),
                JsonSerializer.Serialize(Assert.Single(tick.Goals.Single().CriterionEvidenceObligations!).PlannerSources));

        await f.Repository.TransactGoalStateAsync(f.Goal.Id, (stored, _) =>
        {
            Assert.NotNull(stored);
            var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored.Goal], stored.HumanInputRequests));
            if (terminalStatus == GoalStatus.Cancelled)
                kernel.CancelGoal(f.Goal.Id, "Stop before stale ownership reconciliation is saved.");
            else
                kernel.SupersedeGoal(f.Goal.Id, "Supersede before stale ownership reconciliation is saved.");
            var saved = kernel.ExportSnapshot();
            return Task.FromResult((true, (GoalStateSnapshot?)new GoalStateSnapshot(saved.Goals.Single(), saved.HumanInputRequests), true));
        });
        var fresh = await f.Repository.LoadAsync();
        var stopped = fresh.GetGoal(f.Goal.Id);
        Assert.Equal(terminalStatus, stopped.Status);
        Assert.Null(stopped.ExplicitDisposition);
        Assert.Equal(JsonSerializer.Serialize(baseline.CriterionEvidenceObligations),
            JsonSerializer.Serialize(fresh.ExportGoalSnapshot(f.Goal.Id).CriterionEvidenceObligations));
        Assert.Equal(WorkTaskStatus.Failed, fresh.GetTask(f.Goal.Id, f.Reviewer.Id).Status);
        var before = JsonSerializer.Serialize(fresh.ExportSnapshot());

        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline, tick.Goals.Single(), tick.HumanInputRequests)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Skipped, result.Disposition);
        Assert.Contains("Planner ownership projection cannot change a stored terminal goal", result.Message);
        f.Kernel = await f.Repository.LoadAsync();
        Assert.Equal(before, JsonSerializer.Serialize(f.Kernel.ExportSnapshot()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentBriefOrPlannerRetryRequiresFreshGoalRevalidation(bool plannerRetry)
    {
        using var f = new Fixture();
        f.Plan();
        var snapshot = f.Kernel.ExportSnapshot();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = [snapshot.Goals[0] with { CriterionEvidenceObligations = [] }] });
        f.Review();
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.True(f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "stale ownership projection"));
        var tick = f.Kernel.ExportSnapshot();
        await f.Repository.TransactGoalStateAsync(f.Goal.Id, (stored, _) =>
        {
            var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!.Goal], stored.HumanInputRequests));
            if (plannerRetry) kernel.RetryTask(f.Goal.Id, f.Planner.Id, "Plan against fresh evidence.");
            else kernel.ReviseGoalBrief(f.Goal.Id, "Revised authoritative source and execution contract.");
            var saved = kernel.ExportSnapshot();
            return Task.FromResult((true, (GoalStateSnapshot?)new GoalStateSnapshot(saved.Goals.Single(), saved.HumanInputRequests), true));
        });
        var fresh = await f.Repository.LoadAsync();
        var before = JsonSerializer.Serialize(fresh.ExportSnapshot());
        var result = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline, tick.Goals.Single(), tick.HumanInputRequests)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Skipped, result.Disposition);
        Assert.Contains("changed stored brief, criteria or Planner round", result.Message);
        f.Kernel = await f.Repository.LoadAsync();
        Assert.Equal(before, JsonSerializer.Serialize(f.Kernel.ExportSnapshot()));
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeferredReviewNeedsSuccessfulFullGateAtMatchingCandidate(bool passed, bool staleCandidate)
    {
        using var f = new Fixture();
        f.Plan();
        f.Review();
        await f.Reload();
        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate));
        var gateCandidate = staleCandidate ? new string('b', 40) : Candidate;
        var landCalls = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(passed, [], BranchHeadSha: gateCandidate),
            resolveAcceptanceHeads: _ => (gateCandidate, new string('c', 40)),
            land: goal =>
            {
                landCalls++;
                return new LandingResult(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });
        driver.BeginTick(f.Kernel, 1);
        var result = driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(passed && !staleCandidate ? 1 : 0, landCalls);
        var obligation = Assert.Single(f.Goal.CriterionEvidenceObligations);
        if (passed && !staleCandidate)
        {
            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Equal(CriterionEvidenceState.Satisfied, obligation.State);
            Assert.Equal($"full-acceptance:{Candidate}", obligation.ReceiptId);
            Assert.Empty(f.Goal.GetLandingPrerequisiteObligations(Candidate));
        }
        else
        {
            Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
            Assert.Null(obligation.ReceiptId);
            Assert.Single(f.Goal.GetLandingPrerequisiteObligations(gateCandidate));
        }
    }

    [Fact]
    public async Task FocusedOrWrongScopeReceiptCannotCloseProjectedFullGateObligation()
    {
        using var f = new Fixture();
        f.Plan();
        f.Review();
        var obligation = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Throws<InvalidOperationException>(() => f.Kernel.RecordCriterionEvidence(f.Goal.Id, obligation.Id,
            CriterionEvidenceOwner.Acceptance, Candidate, "focused-only", "focused-tests", true, "Focused tests passed"));
        await f.Reload();
        Assert.Equal(CriterionEvidenceState.Pending, Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate)).State);
        Assert.Throws<InvalidOperationException>(() => f.Kernel.CompleteGoal(f.Goal.Id, "focused evidence"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), nameof(PlannerAcceptanceOwnershipLifecycleTests), Guid.NewGuid().ToString("N"));
        private readonly GoalId goalId;
        private readonly TaskId plannerId;
        private readonly TaskId reviewerId;
        internal AgentOrchestratorKernel Kernel { get; set; } = new();
        internal SqliteOrchestratorStateRepository Repository { get; }
        internal Goal Goal => Kernel.GetGoal(goalId);
        internal TaskSpec Planner => Kernel.GetTask(goalId, plannerId);
        internal TaskSpec Reviewer => Kernel.GetTask(goalId, reviewerId);
        internal Fixture()
        {
            Directory.CreateDirectory(root);
            var database = Path.Combine(root, "state.db");
            _ = StateDbMigrations.EnsureUpToDate(database);
            Repository = new SqliteOrchestratorStateRepository(database);
            var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
            var reviewer = new TaskSpec(TaskId.New(), "Review source safety", AgentRole.Reviewer);
            plannerId = planner.Id;
            reviewerId = reviewer.Id;
            var goal = Kernel.CreateGoal("Preserve source review while Acceptance executes checks", [planner, reviewer]);
            goalId = goal.Id;
            Kernel.RecordGoalRefinement(goalId, new RefinedSpec(goal.Objective,
                ["Preserve source safety and execute the regression suite."], VerificationClass.TestVerifiable, [], []));
            Kernel.ActivateGoal(goalId, AgentCatalog.Default().Agents);
            Kernel.RecordTaskDispatch(goalId, plannerId, new TaskDispatchRecord("fixture", "plan", root, DateTimeOffset.UtcNow));
        }
        internal void RetryPlan()
        {
            Kernel.RetryTask(goalId, plannerId, "Replace the current Planner round.");
            Kernel.RecordTaskDispatch(goalId, plannerId, new TaskDispatchRecord("fixture", "plan", root, DateTimeOffset.UtcNow));
        }
        internal void Plan(string? batch = null, int exitCode = 0) => Kernel.RecordDispatchExecutionResult(goalId, plannerId,
            new TaskVerificationRecord("plan", root, exitCode, (batch ?? Request) + "\n" + Result, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, DispatchAttemptAt: Planner.LastDispatch!.DispatchedAt));
        internal void Review()
        {
            Kernel.RecordTaskDispatch(goalId, reviewerId, new TaskDispatchRecord("fixture", "review", root, DateTimeOffset.UtcNow));
            Kernel.RecordDispatchBaseCommit(goalId, reviewerId, Candidate);
            var output = Result.Replace("END_WORKER_RESULT", """
                findings: []
                touched_anchors: []
                criteria_verdicts: [{"criterion_index":0,"verdict":"not-verifiable","evidence":"Source safety inspected; Acceptance must execute candidate checks"}]
                verdict: pass
                END_WORKER_RESULT
                """, StringComparison.Ordinal);
            Kernel.RecordDispatchExecutionResult(goalId, reviewerId, new TaskVerificationRecord("review", root, 0,
                output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true, ReviewedCommit: Candidate,
                DispatchAttemptAt: Reviewer.LastDispatch!.DispatchedAt));
        }
        internal async Task Reload()
        {
            await Repository.SaveAsync(Kernel);
            Kernel = await Repository.LoadAsync();
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
