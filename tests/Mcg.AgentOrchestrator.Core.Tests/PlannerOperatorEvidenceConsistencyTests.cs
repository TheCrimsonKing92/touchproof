using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns its in-memory goal, requests and providers.
public sealed class PlannerOperatorEvidenceConsistencyTests
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Criterion = "Supply the retained corpus provenance and empirical report.";
    private const string WorkerCriterion = "Preserve deterministic source validation.";
    private const string Request = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"corpus","availability":"post-implementation","owner":"operator","needed":"Supply retained corpus evidence","reason":"Operator observes the retained store"}
        """;
    private const string Result = "WORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: deferred - PlannerOperatorEvidenceConsistencyTests\ncommit: none\nblockers: none\nmodel_fit: fixture/model - adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";

    [Fact]
    public void MissingAuthorityConsolidatesRequestsBeforeDispatchAndReplayPreservesWork()
    {
        var f = new Fixture();
        f.Plan(Request + "\n" + Request.Replace("\"corpus\"", "\"report\"", StringComparison.Ordinal));
        var before = JsonSerializer.Serialize(f.Kernel.ExportSnapshot());
        var hold = Assert.IsType<PlannerOperatorEvidenceConsistencyHold>(f.Hold);
        Assert.Equal(2, hold.Discrepancies.Count);
        Assert.All(hold.Discrepancies, item =>
        {
            Assert.Equal(PlannerOperatorEvidenceProblem.MissingOwnership, item.Problem);
            Assert.Equal(1, item.Source.CriterionIndex);
            Assert.Equal(1, item.CriterionVersion);
            Assert.Equal(f.Goal.Id.Value, item.Source.GoalId);
            Assert.Equal(f.Planner.Id.Value, item.Source.RequestingTaskId);
            Assert.NotNull(item.RequestId);
        });
        Assert.Contains("criterion-evidence-map", hold.Render());
        Assert.Contains("Planner brief amendment", hold.Render());
        for (var replay = 0; replay < 3; replay++)
        {
            Assert.Equal(hold.Identity, f.Hold!.Identity);
            var error = Assert.Throws<InvalidOperationException>(() => f.DispatchReview());
            Assert.Contains("MissingOwnership", error.Message);
            Assert.Null(f.Reviewer.LastDispatch);
            Assert.Equal(WorkTaskStatus.Assigned, f.Reviewer.Status);
            Assert.False(f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "repeat readiness"));
        }
        Assert.Equal(before, JsonSerializer.Serialize(f.Kernel.ExportSnapshot()));
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
        Assert.Equal(0, f.Reviewer.CriterionRetryCount);
        Assert.Equal(WorkTaskStatus.Completed, f.Planner.Status);
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(f.Kernel.ExportSnapshot());
        Assert.Equal(hold.Identity, f.Hold!.Identity);
    }

    [Theory]
    [InlineData("structured")]
    [InlineData("marker")]
    [InlineData("mapping")]
    public void AuthoritativeOperatorOwnershipAllowsDeferralButRequiresCandidateReceipt(string authority)
    {
        var f = new Fixture(authority);
        f.Plan();
        if (authority == "mapping")
        {
            Assert.NotNull(f.Hold);
            f.Map();
        }
        var obligation = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Null(f.Hold);
        Assert.False(VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(f.Goal, 0));
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "preserve authoritative obligation");
        Assert.Equal(obligation, Assert.Single(f.Goal.CriterionEvidenceObligations));
        f.Review();
        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        Assert.Equal(GoalStatus.Verified, f.Goal.Status);
        Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate));
        Assert.Throws<InvalidOperationException>(() => f.Kernel.CompleteGoal(f.Goal.Id, "review is not evidence"));
        // Answer text and the request's completion never satisfy the obligation.
        f.Kernel.SubmitHumanInput(Assert.Single(f.Kernel.HumanInputRequests).Id, "Observation will follow.");
        Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate));
        Assert.Null(f.Hold);
        f.Kernel.RecordCriterionEvidence(f.Goal.Id, obligation.Id, CriterionEvidenceOwner.Operator,
            Candidate, "operator-observation", obligation.RequiredScope, true, "Bound observation receipt");
        var satisfied = Assert.Single(f.Goal.CriterionEvidenceObligations);
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "preserve satisfied receipt");
        Assert.Equal(satisfied, Assert.Single(f.Goal.CriterionEvidenceObligations));
        Assert.Empty(f.Goal.GetLandingPrerequisiteObligations(Candidate));
        Assert.Single(f.Goal.GetLandingPrerequisiteObligations(new string('b', 40)));
        f.Kernel.CompleteGoal(f.Goal.Id, "Bound operator evidence supplied.");
        Assert.Equal(GoalStatus.Completed, f.Goal.Status);
    }

    [Theory]
    [InlineData("acceptance")]
    [InlineData("ambiguous")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public void ConflictingDeclarationsCannotExemptCriterion(string conflict)
    {
        var f = new Fixture(conflict == "acceptance" ? "acceptance" : "structured");
        f.Plan();
        if (conflict != "acceptance")
        {
            var snapshot = f.Kernel.ExportSnapshot();
            var saved = snapshot.Goals[0];
            var obligation = Assert.Single(saved.CriterionEvidenceObligations!);
            if (conflict == "ambiguous")
            {
                var spec = saved.RefinedSpecVersions!.Single();
                saved = saved with { RefinedSpecVersions = [spec with { Spec = spec.Spec with
                { AcceptanceGateOwnedAcceptanceCriteria = [Criterion] } }] };
            }
            else saved = saved with { CriterionEvidenceObligations = conflict == "duplicate"
                ? [obligation, obligation] : [obligation with { Owner = CriterionEvidenceOwner.Unknown }] };
            f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [saved] });
        }
        var problem = Assert.Single(f.Hold!.Discrepancies);
        Assert.Equal(PlannerOperatorEvidenceProblem.ConflictingOwnership, problem.Problem);
        Assert.Contains("provenance=", problem.Declaration);
        Assert.Throws<InvalidOperationException>(() => f.DispatchReview());
        if (conflict != "acceptance")
            Assert.True(VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(f.Goal, 0));
        Assert.Null(f.Reviewer.LastVerification);
        Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate).Select(item => item.CriterionIndex).Distinct());
    }

    [Theory]
    [InlineData("Observe behavior. REAL-WORLD-DEPENDENT.")]
    [InlineData("Operator observes the result after readiness.")]
    public void RequestAndWeakProseNeverEstablishAuthority(string criterion)
    {
        var f = new Fixture(criterion: criterion);
        f.Plan();
        Assert.Equal(PlannerOperatorEvidenceProblem.MissingOwnership, Assert.Single(f.Hold!.Discrepancies).Problem);
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
        Assert.True(VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(f.Goal, 0));
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("task")]
    [InlineData("brief")]
    [InlineData("index")]
    [InlineData("payload")]
    [InlineData("unbound")]
    public void ForeignOrUnboundRecordedRequestCannotMatchAcceptedRound(string corruption)
    {
        var f = new Fixture();
        f.Plan();
        var snapshot = f.Kernel.ExportSnapshot();
        var request = Assert.Single(snapshot.HumanInputRequests);
        var identity = request.PlannerIdentity!;
        identity = corruption switch
        {
            "goal" => identity with { GoalId = GoalId.New().Value },
            "task" => identity with { RequestingTaskId = f.Reviewer.Id.Value },
            "brief" => identity with { BriefVersion = 2 },
            "index" => identity with { CriterionIndex = 2 },
            _ => identity
        };
        request = request with { PlannerIdentity = corruption == "unbound" ? null : identity,
            PlannerEvidence = corruption == "payload" ? request.PlannerEvidence! with { Needed = "Different request" } : request.PlannerEvidence };
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { HumanInputRequests = [request] });
        Assert.Equal(PlannerOperatorEvidenceProblem.InvalidRequestProvenance, Assert.Single(f.Hold!.Discrepancies).Problem);
        Assert.Throws<InvalidOperationException>(() => f.DispatchReview());
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
        Assert.True(VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(f.Goal, 0));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("retry")]
    [InlineData("malformed")]
    [InlineData("failed")]
    public void OnlyCurrentAcceptedPlannerRoundParticipates(string state)
    {
        var f = new Fixture();
        if (state is "malformed" or "failed") f.Plan(state == "malformed" ? "PLANNER_EVIDENCE_REQUEST: {bad}" : Request,
            state == "failed" ? 1 : 0);
        else
        {
            f.Plan();
            Assert.NotNull(f.Hold);
            if (state == "revision") f.Kernel.ReviseGoalBrief(f.Goal.Id, "Replace the current brief without reusing requests.");
            else f.Kernel.RetryTask(f.Goal.Id, f.Planner.Id, "Replace the accepted Planner round.");
        }
        Assert.Null(f.Hold);
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
        Assert.True(VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(f.Goal, 0));
    }

    [Fact]
    public void IndependentWorkerCriterionRemainsEnforcedAfterOwnershipResolution()
    {
        var f = new Fixture(twoCriteria: true);
        f.Plan();
        Assert.Single(f.Hold!.Discrepancies);
        f.Map();
        Assert.Null(f.Hold);
        f.Review(workerVerdict: "not-verifiable");
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        Assert.True(VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(f.Goal, 1));
        Assert.False(VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(f.Goal, 0));
        Assert.Contains(f.Goal.Timeline, item => item.Kind == ProgressKind.TaskFailed &&
            item.Message.Contains("criterion_index=1", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitOperatorMappingResolvesAnOwnershipConflictWithoutDuplicateObligations()
    {
        var f = new Fixture("acceptance");
        f.Plan();
        Assert.Equal(PlannerOperatorEvidenceProblem.ConflictingOwnership, Assert.Single(f.Hold!.Discrepancies).Problem);
        f.Map();
        Assert.Null(f.Hold);
        var obligation = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceOwner.Operator, obligation.Owner);
        Assert.Null(obligation.PlannerSources);
        Assert.Null(obligation.ReceiptId);
        Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate));
    }

    [Fact]
    public async Task ApiReadinessRefusesBeforeProviderEffects()
    {
        var f = new Fixture();
        f.Plan();
        var provider = new Provider();
        var runner = new AgentTaskRunner(f.Kernel, f.Agents, new InMemoryModelProviderRegistry([provider]));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(f.Goal.Id, f.Reviewer.Id));
        Assert.Contains(PlannerOperatorEvidenceConsistencyHold.State, error.Message);
        Assert.Equal(0, provider.Calls);
        Assert.Null(f.Reviewer.LastExecution);
        Assert.Equal(WorkTaskStatus.Assigned, f.Reviewer.Status);
    }

    [Fact]
    public async Task ApiPlannerAcceptanceProducesTheSameMissingOwnershipHold()
    {
        var f = new Fixture(dispatchPlanner: false);
        var provider = new Provider(Request + "\n" + Result);
        var runner = new AgentTaskRunner(f.Kernel, f.Agents, new InMemoryModelProviderRegistry([provider]));
        await runner.RunAsync(f.Goal.Id, f.Planner.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(WorkTaskStatus.Completed, f.Planner.Status);
        Assert.Equal(PlannerOperatorEvidenceProblem.MissingOwnership, Assert.Single(f.Hold!.Discrepancies).Problem);
        var hash = f.Planner.LastExecution!.PlannerEvidenceCriteriaVersionHash;
        Assert.Equal(EffectiveAcceptanceCriteriaVersion.ComputeForGoal(f.Goal), hash);
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(f.Kernel.ExportSnapshot());
        Assert.Equal(hash, f.Planner.LastExecution!.PlannerEvidenceCriteriaVersionHash);
        Assert.Equal(PlannerOperatorEvidenceProblem.MissingOwnership, Assert.Single(f.Hold!.Discrepancies).Problem);
        Assert.Throws<InvalidOperationException>(() => f.DispatchReview());
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
    }

    [Fact]
    public async Task ApiCriteriaIdentityCannotBeBorrowedAfterAnInPlaceRefinementChange()
    {
        var f = new Fixture(dispatchPlanner: false);
        var runner = new AgentTaskRunner(f.Kernel, f.Agents,
            new InMemoryModelProviderRegistry([new Provider(Request + "\n" + Result)]));
        await runner.RunAsync(f.Goal.Id, f.Planner.Id, TestContext.Current.CancellationToken);
        Assert.Equal(PlannerOperatorEvidenceProblem.MissingOwnership, Assert.Single(f.Hold!.Discrepancies).Problem);
        f.Kernel.SetGoalRefinedSpec(f.Goal.Id, new RefinedSpec(f.Goal.Objective,
            ["A different retained observation."], VerificationClass.TestVerifiable, [], []));
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(f.Kernel.ExportSnapshot());
        Assert.Equal(PlannerOperatorEvidenceProblem.InvalidRequestProvenance, Assert.Single(f.Hold!.Discrepancies).Problem);
        Assert.Throws<InvalidOperationException>(() => f.DispatchReview());
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
    }

    [Fact]
    public void CriterionVersionOrTextChangeCannotReuseAcceptedRequest()
    {
        var f = new Fixture();
        f.Plan();
        Assert.NotNull(f.Hold);
        f.Kernel.RecordGoalRefinement(f.Goal.Id, new RefinedSpec(f.Goal.Objective,
            ["A different operator observation."], VerificationClass.TestVerifiable, [], [])
        { OperatorOwnedAcceptanceCriteria = ["A different operator observation."] });
        var discrepancy = Assert.Single(f.Hold!.Discrepancies);
        Assert.Equal(2, discrepancy.CriterionVersion);
        Assert.Equal(PlannerOperatorEvidenceProblem.InvalidRequestProvenance, discrepancy.Problem);
        Assert.Throws<InvalidOperationException>(() => f.DispatchReview());
        Assert.Equal(0, f.Reviewer.CriterionRetryCount);
    }

    [Fact]
    public void ExistingPostLandingLifecycleIsPreservedWithoutRequestDerivedRephasing()
    {
        var f = new Fixture("postlanding");
        f.Plan();
        var duty = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Null(f.Hold);
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "retain lifecycle semantics");
        Assert.Equal(duty, Assert.Single(f.Goal.CriterionEvidenceObligations));
        Assert.Equal(CriterionEvidencePhase.PostLanding, duty.Phase);
        Assert.NotNull(duty.ObservationScope);
        Assert.Null(duty.Publication);
        Assert.Null(duty.ReceiptId);
    }

    [Theory]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    [InlineData(GoalStatus.Failed)]
    public void TerminalGoalReadinessNeverMutatesAuthority(GoalStatus status)
    {
        var f = new Fixture();
        f.Plan();
        var snapshot = f.Kernel.ExportSnapshot();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [snapshot.Goals[0] with { Status = status }] });
        var before = JsonSerializer.Serialize(f.Kernel.ExportSnapshot());
        Assert.Null(f.Hold);
        Assert.Equal(before, JsonSerializer.Serialize(f.Kernel.ExportSnapshot()));
    }

    private sealed class Provider(string output = Result) : IModelProvider
    {
        public string ProviderName => "OpenAI";
        internal int Calls { get; private set; }
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ModelResponse(output, new ModelUsage(1, 1), "stop"));
        }
    }

    private sealed class Fixture
    {
        internal AgentOrchestratorKernel Kernel { get; set; } = new();
        internal IReadOnlyList<AgentDefinition> Agents { get; } = DefaultAgents();
        private readonly GoalId goalId;
        private readonly TaskId plannerId;
        private readonly TaskId reviewerId;
        private readonly bool twoCriteria;
        internal Goal Goal => Kernel.GetGoal(goalId);
        internal TaskSpec Planner => Kernel.GetTask(goalId, plannerId);
        internal TaskSpec Reviewer => Kernel.GetTask(goalId, reviewerId);
        internal PlannerOperatorEvidenceConsistencyHold? Hold => Kernel.GetPlannerOperatorEvidenceConsistencyHold(goalId);
        internal Fixture(string? authority = null, bool twoCriteria = false, string criterion = Criterion, bool dispatchPlanner = true)
        {
            this.twoCriteria = twoCriteria;
            if (authority == "marker") criterion += " Operator owns observation.";
            var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
            var reviewer = new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer);
            plannerId = planner.Id;
            reviewerId = reviewer.Id;
            var goal = Kernel.CreateGoal("Keep operator evidence authoritative", [planner, reviewer]);
            goalId = goal.Id;
            Kernel.RecordGoalRefinement(goalId, new RefinedSpec(goal.Objective,
                twoCriteria ? [criterion, WorkerCriterion] : [criterion], VerificationClass.TestVerifiable, [], [])
            {
                OperatorOwnedAcceptanceCriteria = authority is "structured" or "postlanding" ? [criterion] : [],
                AcceptanceGateOwnedAcceptanceCriteria = authority == "acceptance" ? [criterion] : [],
                CriterionEvidenceDeclarations = authority == "postlanding"
                    ? [new(criterion, CriterionEvidencePhase.PostLanding,
                        new(CriterionObservationKind.Runtime, "Observe deployed behavior", "fixture deployment"), Atomic: true)] : []
            });
            Kernel.ActivateGoal(goalId, Agents);
            if (dispatchPlanner)
                Kernel.RecordTaskDispatch(goalId, plannerId, new TaskDispatchRecord("fixture", "plan", "C:\\fixture", DateTimeOffset.UtcNow));
        }
        internal void Plan(string batch = Request, int exitCode = 0) => Kernel.RecordDispatchExecutionResult(goalId, plannerId,
            new TaskVerificationRecord("plan", "C:\\fixture", exitCode, batch + "\n" + Result, "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, DispatchAttemptAt: Planner.LastDispatch!.DispatchedAt));
        internal void Map() => Kernel.MapCriterionEvidenceOwner(goalId, 0, 1, CriterionEvidenceOwner.Operator,
            "operator", "retained corpus observation", expectedCandidateSha: Candidate);
        internal void DispatchReview() => Kernel.RecordTaskDispatch(goalId, reviewerId,
            new TaskDispatchRecord("fixture", "review", "C:\\fixture", DateTimeOffset.UtcNow));
        internal void Review(string workerVerdict = "met")
        {
            DispatchReview();
            Kernel.RecordDispatchBaseCommit(goalId, reviewerId, Candidate);
            var verdicts = "[{\"criterion_index\":0,\"verdict\":\"not-verifiable\",\"evidence\":\"Operator observation remains pending\"}" +
                (twoCriteria ? $",{{\"criterion_index\":1,\"verdict\":\"{workerVerdict}\",\"evidence\":\"Source checked\"}}" : "") + "]";
            var output = Result.Replace("END_WORKER_RESULT", "findings: []\ntouched_anchors: []\ncriteria_verdicts: " +
                verdicts + "\nverdict: pass\nEND_WORKER_RESULT", StringComparison.Ordinal);
            Kernel.RecordDispatchExecutionResult(goalId, reviewerId, new TaskVerificationRecord("review", "C:\\fixture", 0,
                output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true, ReviewedCommit: Candidate,
                DispatchAttemptAt: Reviewer.LastDispatch!.DispatchedAt));
        }
    }
}
