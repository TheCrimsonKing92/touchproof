using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: private SQLite stores, deterministic delegate effects, no workers.
public sealed class PlannerOperatorEvidenceReadinessTests
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Criterion = "Supply retained corpus provenance and empirical observations.";
    private const string GateCriterion = "Execute the deterministic candidate checks.";
    private const string Request = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"corpus","availability":"post-implementation","owner":"operator","needed":"Supply retained corpus observations","reason":"Operator owns retained-store access"}
        """;
    private const string Result = "WORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: deferred - PlannerOperatorEvidenceReadinessTests\ncommit: none\nblockers: none\nmodel_fit: fixture/model - adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";

    [Fact]
    public async Task OperatorFixture_HoldReplayMappingAndResumedReadinessPreserveAcceptedWork()
    {
        using var f = new Fixture();
        f.Plan();
        var developer = JsonSerializer.Serialize(f.Developer);
        var reviewer = JsonSerializer.Serialize(f.Reviewer);
        var gate = JsonSerializer.Serialize(f.Goal.CriterionEvidenceObligations);
        var retries = f.Reviewer.CriterionRetryCount;
        string? identity = null;
        for (var tick = 1; tick <= 3; tick++)
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(f.Tick(tick).Outcome);
            var consistency = Assert.IsType<PlannerOperatorEvidenceConsistencyHold>(held.OperatorEvidenceConsistency);
            identity ??= consistency.Identity;
            Assert.Equal(identity, held.StableIdentity);
            Assert.Equal(PlannerOperatorEvidenceProblem.MissingOwnership, Assert.Single(consistency.Discrepancies).Problem);
            Assert.Contains("criterion 1 (v1)", held.Reason);
            Assert.Contains("key=corpus", held.Reason);
            Assert.Equal(ConductorHoldOwner.Operator, held.Owner);
            Assert.Equal(0, f.Dispatches);
            Assert.Equal(0, f.Escalations);
            Assert.Equal(retries, f.Reviewer.CriterionRetryCount);
            Assert.Equal(reviewer, JsonSerializer.Serialize(f.Reviewer));
            Assert.Empty(f.Reviewer.DispatchHistory);
            Assert.Equal(WorkTaskStatus.Completed, f.Developer.Status);
            Assert.Equal(developer, JsonSerializer.Serialize(f.Developer));
            Assert.Equal(gate, JsonSerializer.Serialize(f.Goal.CriterionEvidenceObligations));
            await f.Reload();
            Assert.Equal(PlannerOperatorEvidenceConsistencyHold.State, f.Goal.CurrentHold!.State);
        }
        f.Map();
        await f.Reload();
        Assert.Null(f.Kernel.GetPlannerOperatorEvidenceConsistencyHold(f.Goal.Id));
        Assert.Equal(developer, JsonSerializer.Serialize(f.Developer));
        Assert.Equal(gate, JsonSerializer.Serialize(f.Goal.CriterionEvidenceObligations.Where(item => item.CriterionIndex == 1)));
        Assert.IsType<ConductorAdvanceOutcome.Executed>(f.Tick(4).Outcome);
        Assert.Equal(1, f.Dispatches);
        Assert.Null(f.Goal.CurrentHold);
        var obligation = Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate));
        Assert.Equal(CriterionEvidenceOwner.Operator, obligation.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Assert.Equal(Candidate, obligation.ExpectedCandidateSha);
        Assert.Null(obligation.ReceiptId);
        Assert.Equal(retries, f.Reviewer.CriterionRetryCount);
        Assert.Equal(WorkTaskStatus.Completed, f.Developer.Status);
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("brief")]
    [InlineData("planner")]
    [InlineData("cancel")]
    public async Task StaleTickCannotPublishHoldOverChangedAuthorityOrTerminalState(string change)
    {
        using var f = new Fixture();
        f.Plan();
        await f.Repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportGoalSnapshot(f.Goal.Id);
        Assert.IsType<ConductorAdvanceOutcome.Held>(f.Tick(1).Outcome);
        var staleTick = f.Kernel.ExportSnapshot();
        await f.Repository.TransactGoalStateAsync(f.Goal.Id, (stored, _) =>
        {
            var fresh = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([stored!.Goal], stored.HumanInputRequests));
            switch (change)
            {
                case "mapping": fresh.MapCriterionEvidenceOwner(f.Goal.Id, 0, 1, CriterionEvidenceOwner.Operator,
                    "operator", "corpus observation", expectedCandidateSha: Candidate); break;
                case "brief": fresh.ReviseGoalBrief(f.Goal.Id, "New authoritative brief."); break;
                case "planner": fresh.RetryTask(f.Goal.Id, f.Planner.Id, "Replace the accepted plan."); break;
                default: fresh.CancelGoal(f.Goal.Id, "Operator stopped this goal."); break;
            }
            var saved = fresh.ExportSnapshot();
            return Task.FromResult((true, (GoalStateSnapshot?)new GoalStateSnapshot(saved.Goals.Single(), saved.HumanInputRequests), true));
        });
        var expected = JsonSerializer.Serialize((await f.Repository.LoadAsync()).ExportSnapshot());
        var save = Assert.Single(await f.Repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline, staleTick.Goals.Single(), staleTick.HumanInputRequests)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Skipped, save.Disposition);
        Assert.Contains("Planner operator evidence consistency", save.Message);
        Assert.Equal(expected, JsonSerializer.Serialize((await f.Repository.LoadAsync()).ExportSnapshot()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedDispatchCannotStartWhileConsistencyIsHeld(bool background)
    {
        using var f = new Fixture();
        // A prepared dispatch from before the accepted Planner result must also
        // consult current ownership before any process/logging effect.
        f.Kernel.RecordTaskDispatch(f.Goal.Id, f.Reviewer.Id,
            new TaskDispatchRecord("fixture", "must-never-run", f.Root, DateTimeOffset.UtcNow));
        f.Plan();
        var before = JsonSerializer.Serialize(f.Kernel.ExportSnapshot());
        var launches = 0;
        InvalidOperationException error;
        if (background)
        {
            var runner = new BackgroundDispatchRunner(disableProcessStart: false,
                startProcess: _ => { launches++; throw new InvalidOperationException("Unexpected launch"); });
            error = Assert.Throws<InvalidOperationException>(() =>
                runner.TryStartLatestDispatch(f.Kernel, f.Goal.Id, f.Reviewer.Id, Path.Combine(f.Root, "logs")));
        }
        else error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalDispatchRunner().ExecuteLatestDispatchAsync(f.Kernel, f.Goal.Id, f.Reviewer.Id));
        Assert.Contains(PlannerOperatorEvidenceConsistencyHold.State, error.Message);
        Assert.Equal(0, launches);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "logs")));
        Assert.Equal(before, JsonSerializer.Serialize(f.Kernel.ExportSnapshot()));
        Assert.Null(f.Reviewer.LastProcess);
        Assert.Null(f.Reviewer.LastVerification);
    }

    [Fact]
    public async Task ClearingAStoredHoldCannotRestoreAnObsoleteCandidateReceipt()
    {
        using var f = new Fixture();
        f.Plan();
        f.Tick(1);
        f.Map();
        var obligation = f.Goal.CriterionEvidenceObligations.Single(item => item.CriterionIndex == 0);
        f.Kernel.RecordCriterionEvidence(f.Goal.Id, obligation.Id, CriterionEvidenceOwner.Operator,
            Candidate, "observation-a", obligation.RequiredScope, true, "Observed candidate A.");
        var receipt = f.Goal.CriterionEvidenceObligations.Single(item => item.CriterionIndex == 0);
        await f.Reload();
        Assert.Null(f.Kernel.GetPlannerOperatorEvidenceConsistencyHold(f.Goal.Id));
        f.Tick(2);
        Assert.Null(f.Goal.CurrentHold);
        Assert.Empty(f.Goal.GetLandingPrerequisiteObligations(Candidate));
        Assert.NotEmpty(f.Goal.GetLandingPrerequisiteObligations(new string('b', 40)));
        f.Kernel.MapCriterionEvidenceOwner(f.Goal.Id, 0, 1, CriterionEvidenceOwner.Operator,
            "operator", obligation.RequiredScope, expectedCandidateSha: new string('b', 40));
        var rebound = f.Goal.CriterionEvidenceObligations.Single(item => item.CriterionIndex == 0);
        Assert.Equal(CriterionEvidenceState.Pending, rebound.State);
        Assert.Null(rebound.ReceiptId);
        Assert.Equal(receipt.ReceiptId, Assert.Single(rebound.PriorReceipts!).ReceiptId);
        Assert.NotEmpty(f.Goal.GetLandingPrerequisiteObligations(new string('b', 40)));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), nameof(PlannerOperatorEvidenceReadinessTests), Guid.NewGuid().ToString("N"));
        internal AgentOrchestratorKernel Kernel { get; set; } = new();
        internal SqliteOrchestratorStateRepository Repository { get; }
        private readonly GoalId goalId;
        private readonly TaskId plannerId;
        private readonly TaskId developerId;
        private readonly TaskId reviewerId;
        internal Goal Goal => Kernel.GetGoal(goalId);
        internal TaskSpec Planner => Kernel.GetTask(goalId, plannerId);
        internal TaskSpec Developer => Kernel.GetTask(goalId, developerId);
        internal TaskSpec Reviewer => Kernel.GetTask(goalId, reviewerId);
        internal int Dispatches { get; private set; }
        internal int Escalations { get; private set; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            var database = Path.Combine(Root, "state.db");
            _ = StateDbMigrations.EnsureUpToDate(database);
            Repository = new SqliteOrchestratorStateRepository(database);
            var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
            var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            plannerId = planner.Id;
            developerId = developer.Id;
            reviewerId = reviewer.Id;
            var goal = Kernel.CreateGoal("Hold inconsistent operator evidence before review", [planner, developer, reviewer]);
            goalId = goal.Id;
            Kernel.RecordGoalRefinement(goalId, new RefinedSpec(goal.Objective,
                [Criterion, GateCriterion], VerificationClass.TestVerifiable, [], [])
            { AcceptanceGateOwnedAcceptanceCriteria = [GateCriterion] });
            Kernel.ActivateGoal(goalId, AgentCatalog.Default().Agents);
            // Retained accepted work and a candidate-compatible gate receipt are
            // independent of the missing operator declaration.
            Kernel.ReportTaskProgress(goalId, developerId, WorkTaskStatus.Completed, "Implemented before ownership reconciliation.");
            Kernel.RecordTaskVerification(goalId, developerId,
                new TaskVerificationRecord("fixture verification", Root, 0, "ok", "", DateTimeOffset.UtcNow));
            Assert.Equal(WorkTaskStatus.Completed, Developer.Status);
            var gate = Kernel.MapCriterionEvidenceOwner(goalId, 1, 1, CriterionEvidenceOwner.Acceptance,
                "operator", expectedCandidateSha: Candidate);
            Kernel.RecordCriterionEvidence(goalId, gate.Id, CriterionEvidenceOwner.Acceptance,
                Candidate, "full-gate-a", gate.RequiredScope, true, "Retained full candidate gate receipt.");
            Kernel.RecordTaskDispatch(goalId, plannerId, new TaskDispatchRecord("fixture", "plan", Root, DateTimeOffset.UtcNow));
        }
        internal void Plan() => Kernel.RecordDispatchExecutionResult(goalId, plannerId,
            new TaskVerificationRecord("plan", Root, 0, Request + "\n" + Result, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, DispatchAttemptAt: Planner.LastDispatch!.DispatchedAt));
        internal void Map() => Kernel.MapCriterionEvidenceOwner(goalId, 0, 1, CriterionEvidenceOwner.Operator,
            "operator", "corpus observation", expectedCandidateSha: Candidate);
        internal ConductorAdvanceResult Tick(int tick)
        {
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                normalizeLifecycleState: (goal, reason) => Kernel.NormalizeGoalLifecycleState(goal.Id, reason),
                dispatchAndStart: _ => { Dispatches++; return DispatchStartOutcome.Started(); },
                writeEscalation: (_, _, _) => Escalations++);
            driver.BeginTick(Kernel, tick);
            var result = driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
            ConductorBatchLoop.TrackGoalOutcome(Kernel, driver, Goal, result.Outcome,
                DateTimeOffset.UtcNow, TimeSpan.MaxValue, [], []);
            return result;
        }
        internal async Task Reload()
        {
            await Repository.SaveAsync(Kernel);
            Kernel = await Repository.LoadAsync();
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
