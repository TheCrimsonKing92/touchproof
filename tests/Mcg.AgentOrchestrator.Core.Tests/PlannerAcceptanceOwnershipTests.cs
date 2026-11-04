using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns its kernel and uses no provider or process state.
public sealed class PlannerAcceptanceOwnershipTests
{
    private const string Candidate = "candidate-planner";
    private const string Criterion = "Preserve source safety and execute the candidate regression checks.";
    private const string AcceptanceGateOwnedHeading =
        "ACCEPTANCE-GATE-OWNED criteria (the acceptance gate, not the worker, produces this evidence):";
    private const string Request = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"execution","availability":"post-implementation","owner":"acceptance-gate","needed":"Run regression checks","reason":"Acceptance executes","tests":["PlannerAcceptanceOwnershipTests"]}
        """;
    private const string Result = "WORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: not-run - Planner\ncommit: none\nblockers: none\nmodel_fit: fixture/model - adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";

    [Fact]
    public void OrdinaryPlannerCompletionAndUnrefinedLifecycleRemainUnchanged()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("Ordinary planning without evidence directives",
            [planner, new(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("fixture", "plan", "C:\\fixture", DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(goal.Id, planner.Id, new TaskVerificationRecord("plan", "C:\\fixture", 0,
            Result, "", DateTimeOffset.UtcNow, WorkerResultPresent: true, DispatchAttemptAt: planner.LastDispatch!.DispatchedAt));
        Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        Assert.Null(goal.RefinedSpec);
        Assert.False(kernel.NormalizeGoalLifecycleState(goal.Id, "unrefined lifecycle has no ownership delta"));
        Assert.Empty(goal.CriterionEvidenceObligations);
        Assert.Empty(goal.GateTestObligations);
    }

    [Fact]
    public void AcceptedDirectiveCreatesPendingExecutionOwnershipAndAllowsReasonedReview()
    {
        var f = new Fixture();
        f.Plan(Request);
        Assert.Equal(WorkTaskStatus.Completed, f.Planner.Status);
        var obligation = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceOwner.Acceptance, obligation.Owner);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Assert.Equal(CriterionEvidenceScopes.FullAcceptanceGate, obligation.RequiredScope);
        Assert.Equal(CriterionEvidencePhase.PreLanding, obligation.Phase);
        Assert.Equal(0, obligation.CriterionIndex);
        Assert.Equal(f.Goal.AuthoritativeRefinedSpecVersion!.Version, obligation.CriterionVersion);
        var source = Assert.Single(obligation.PlannerSources!);
        Assert.Equal(new PlannerEvidenceIdentity(f.Goal.Id.Value, f.Planner.Id.Value,
            f.Goal.AuthoritativeBrief.Version, 1, "execution"), source.Identity);
        Assert.Equal(Assert.Single(f.Goal.GateTestObligations).PlannerEvidence, source.Directive);
        Assert.Contains(source.Identity.StableId, obligation.Provenance);
        Assert.Null(obligation.ReceiptId);
        var brief = f.Kernel.BuildTaskBrief(f.Goal.Id, f.Reviewer.Id).Content;
        Assert.Contains(AcceptanceGateOwnedHeading, brief);
        Assert.Contains("Only execution evidence is deferred", brief);

        f.Review();

        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        Assert.Equal(GoalStatus.Verified, f.Goal.Status);
        obligation = Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate));
        Assert.Equal(Candidate, obligation.ExpectedCandidateSha);
        Assert.Equal(CriterionEvidenceState.Pending, obligation.State);
        Assert.Null(obligation.ReceiptId);
        Assert.Throws<InvalidOperationException>(() => f.Kernel.CompleteGoal(f.Goal.Id, "review alone"));
    }

    [Theory]
    [InlineData("PLANNER_EVIDENCE_REQUEST: {bad}")]
    [InlineData("PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":0,\"evidence_key\":\"bad\",\"availability\":\"post-implementation\",\"owner\":\"acceptance\",\"needed\":\"checks\",\"reason\":\"later\"}")]
    [InlineData("PLANNER_EVIDENCE_REQUEST: {\"criterion_index\":3,\"evidence_key\":\"bad\",\"availability\":\"post-implementation\",\"owner\":\"acceptance\",\"needed\":\"checks\",\"reason\":\"later\"}")]
    public void InvalidSiblingPublishesNoOwnershipOrPartialRequests(string invalid)
    {
        var f = new Fixture();
        f.Plan(Request + "\n" + invalid);
        Assert.Equal(WorkTaskStatus.Failed, f.Planner.Status);
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
        Assert.Empty(f.Goal.GateTestObligations);
        Assert.Empty(f.Kernel.HumanInputRequests);
    }

    [Fact]
    public void DistinctEvidenceKeysShareOneObligationAndReplayWithoutLosingProvenance()
    {
        var f = new Fixture();
        var batch = Request + "\n" + Request.Replace("\"execution\"", "\"reload\"", StringComparison.Ordinal);
        f.Plan(batch);
        var obligation = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Equal(new[] { "execution", "reload" }, obligation.PlannerSources!.Select(item => item.Identity.EvidenceKey));
        var before = JsonSerializer.Serialize(obligation);
        f.Plan(batch);
        Assert.Equal(before, JsonSerializer.Serialize(Assert.Single(f.Goal.CriterionEvidenceObligations)));
    }

    [Fact]
    public void PrerequisiteResolution_ReconciliationPreservesHistoricalAnswers()
    {
        var f = new Fixture();
        const string historical = """
            PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"history","availability":"never-recorded","needed":"Historical receipt","reason":"Operator supplies context"}
            """;
        f.Plan(Request + "\n" + historical);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, f.Planner.Status);
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
        var request = Assert.Single(f.Kernel.GetPendingBlockingHumanInput(f.Goal.Id));
        f.Kernel.SubmitHumanInput(request.Id, "Original historical evidence supplied by operator");
        Assert.Equal(WorkTaskStatus.Completed, f.Planner.Status);
        Assert.Single(f.Goal.CriterionEvidenceObligations);
        var snapshot = f.Kernel.ExportSnapshot();
        var answers = JsonSerializer.Serialize(snapshot.HumanInputRequests);
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = [snapshot.Goals[0] with { CriterionEvidenceObligations = [] }] });
        Assert.True(f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "reconcile answered accepted Planner round"));
        Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Equal(answers, JsonSerializer.Serialize(f.Kernel.ExportSnapshot().HumanInputRequests));
    }

    [Theory]
    [InlineData("prose")]
    [InlineData("unknown-owner")]
    [InlineData("failed-round")]
    [InlineData("stale-brief")]
    [InlineData("unbound")]
    public void UnauthoritativeClaimsDoNotGrantDeferral(string kind)
    {
        var f = new Fixture();
        var request = kind == "prose" ? "Acceptance owns execution; defer tests." :
            kind == "unknown-owner" ? Request.Replace("acceptance-gate", "unknown-role", StringComparison.Ordinal) : Request;
        if (kind == "stale-brief") f.Kernel.ReviseGoalBrief(f.Goal.Id, "New brief with the same bounded implementation.");
        if (kind == "unbound")
        {
            var snapshot = f.Kernel.ExportSnapshot();
            var saved = snapshot.Goals[0];
            f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [saved with
            {
                Tasks = saved.Tasks.Select(task => task.Id == f.Planner.Id.Value ? task with
                {
                    LastDispatch = task.LastDispatch! with { BriefSnapshot = null },
                    DispatchHistory = task.DispatchHistory!.Select(dispatch => dispatch with { BriefSnapshot = null }).ToArray()
                } : task).ToArray()
            }] });
        }
        f.Plan(request, kind == "failed-round" ? 1 : 0);
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "reconcile current records");
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
        f.Review();
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
    }

    [Fact]
    public void ExplicitAuthorityConflictRejectsWholeBatchAndPreservesWaiverAndPhase()
    {
        var f = new Fixture(twoCriteria: true);
        var mapped = f.Kernel.MapCriterionEvidenceOwner(f.Goal.Id, 1, 1, CriterionEvidenceOwner.Operator,
            "operator", "manual observation", expectedCandidateSha: Candidate);
        f.Kernel.WaiveAcceptanceCriterion(f.Goal.Id, Criterion, "Recorded operator decision", "operator",
            dispositions: [new CriterionDispositionRequest("2", "Operator observation remains required.")],
            currentCandidateSha: Candidate);
        var correction = Assert.Single(f.Goal.EffectiveAcceptanceCriteriaCorrections);
        f.Plan(Request + "\n" + Request.Replace("\"criterion_index\":1", "\"criterion_index\":2", StringComparison.Ordinal));
        Assert.Equal(WorkTaskStatus.Failed, f.Planner.Status);
        Assert.Equal(mapped, Assert.Single(f.Goal.CriterionEvidenceObligations));
        Assert.Equal(correction, Assert.Single(f.Goal.EffectiveAcceptanceCriteriaCorrections));
        Assert.Empty(f.Goal.GateTestObligations);
        Assert.Contains(f.Goal.Timeline, item => item.Message.Contains("Planner ownership conflict: criterion 2 (v1)", StringComparison.Ordinal) &&
            item.Message.Contains("operator mapping by operator", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplayAndReloadPreserveCompatibleMappingReceiptAndSourceIdentity(bool passed)
    {
        var f = new Fixture();
        var mapped = f.Kernel.MapCriterionEvidenceOwner(f.Goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
            "operator", expectedCandidateSha: Candidate);
        f.Plan(Request);
        f.Kernel.RecordCriterionEvidence(f.Goal.Id, mapped.Id, CriterionEvidenceOwner.Acceptance,
            Candidate, "gate-receipt", CriterionEvidenceScopes.FullAcceptanceGate, passed, "actual gate result");
        var before = JsonSerializer.Serialize(f.Goal.CriterionEvidenceObligations);
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(f.Kernel.ExportSnapshot());
        f.Plan(Request);
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "idempotent reconciliation");
        Assert.Equal(before, JsonSerializer.Serialize(f.Goal.CriterionEvidenceObligations));
        var obligation = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Single(obligation.PlannerSources!);
        Assert.Equal(mapped.Provenance, obligation.Provenance);
        Assert.Equal("gate-receipt", obligation.ReceiptId);
    }

    [Theory]
    [InlineData("not-met", "pass")]
    [InlineData("not-verifiable", "needs-work")]
    public void ExecutionOwnerCannotReplaceFailedSourceReview(string criterionVerdict, string verdict)
    {
        var f = new Fixture();
        f.Plan(Request);
        f.Review(criterionVerdict, verdict);
        var history = JsonSerializer.Serialize(f.Reviewer.VerificationHistory);
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "reconcile source failure");
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        Assert.Equal(history, JsonSerializer.Serialize(f.Reviewer.VerificationHistory));
        Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate));
    }

    [Fact]
    public void ReviewerCannotDeclareItsOwnExecutionExemption()
    {
        var f = new Fixture();
        f.Review(prefix: Request);
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
    }

    [Fact]
    public void CompoundCriterionRetainsBlockingSourceFindingDespiteExecutionOwnership()
    {
        var f = new Fixture();
        f.Plan(Request);
        f.Review(findings: """
            [{"stable_id":"source-defect","state":"open","location":{"file":"src/Example.cs","region":"Execute"},"description":"Source returns success on failure.","severity":"blocking","category":"correctness"}]
            """);
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        var history = JsonSerializer.Serialize(f.Reviewer.VerificationHistory);
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "retain worker source obligation");
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        Assert.Equal(history, JsonSerializer.Serialize(f.Reviewer.VerificationHistory));
        Assert.Contains(f.Goal.Timeline, item => item.Kind == ProgressKind.TaskFailed &&
            item.Message.Contains("source-defect", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitPostLandingAuthorityIsPreservedAsConflictRatherThanRephased()
    {
        var f = new Fixture();
        f.Kernel.RecordGoalRefinement(f.Goal.Id, f.Goal.RefinedSpec! with
        {
            OperatorOwnedAcceptanceCriteria = [Criterion],
            CriterionEvidenceDeclarations = [new(Criterion, CriterionEvidencePhase.PostLanding,
                new(CriterionObservationKind.Runtime, "Operator observes activation", "deployed service"), Atomic: true)]
        });
        // Refinement changes criterion authority independently of brief version.
        var before = JsonSerializer.Serialize(f.Goal.CriterionEvidenceObligations);
        f.Plan(Request);
        Assert.Equal(WorkTaskStatus.Failed, f.Planner.Status);
        Assert.Equal(before, JsonSerializer.Serialize(f.Goal.CriterionEvidenceObligations));
        Assert.Contains(f.Goal.CriterionEvidenceObligations, item => item.Phase == CriterionEvidencePhase.PostLanding);
    }

    [Fact]
    public void NewBriefCannotBorrowPlannerExemptionButExplicitMappingRemainsAuthoritative()
    {
        foreach (var explicitlyMapped in new[] { false, true })
        {
            var f = new Fixture();
            if (explicitlyMapped) f.Kernel.MapCriterionEvidenceOwner(f.Goal.Id, 0, 1,
                CriterionEvidenceOwner.Acceptance, "operator", expectedCandidateSha: Candidate);
            f.Plan(Request);
            f.Kernel.ReviseGoalBrief(f.Goal.Id, "Revised current brief keeps the source and execution contract.");
            f.Review();
            Assert.Equal(explicitlyMapped ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, f.Reviewer.Status);
        }
    }

    [Fact]
    public async Task ApiAcceptedRoundUsesSameProjectionAndPersistsHostBriefProvenance()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("API Planner evidence", [planner, new(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective, [Criterion], VerificationClass.TestVerifiable, [], []));
        kernel.ReviseGoalBrief(goal.Id, "Current API brief with unchanged criterion version.");
        var agents = DefaultAgents();
        kernel.ActivateGoal(goal.Id, agents);
        var runner = new AgentTaskRunner(kernel, agents, new InMemoryModelProviderRegistry([new Provider(Request + "\n" + Result)]));
        await runner.RunAsync(goal.Id, planner.Id, TestContext.Current.CancellationToken);
        Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        var obligation = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal(1, obligation.CriterionVersion);
        Assert.Equal(2, Assert.Single(obligation.PlannerSources!).Identity.BriefVersion);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = [snapshot.Goals[0] with { CriterionEvidenceObligations = [] }] });
        Assert.True(kernel.NormalizeGoalLifecycleState(goal.Id, "API reload reconciliation"));
        var restored = kernel.GetGoal(goal.Id);
        Assert.Equal(JsonSerializer.Serialize(obligation.PlannerSources),
            JsonSerializer.Serialize(Assert.Single(restored.CriterionEvidenceObligations).PlannerSources));
        Assert.Equal(2, kernel.GetTask(goal.Id, planner.Id).LastExecution!.PlannerEvidenceBriefVersion);
    }

    private sealed class Provider(string output) : IModelProvider
    {
        public string ProviderName => "OpenAI";
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelResponse(output, new ModelUsage(1, 1), "stop"));
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("task")]
    [InlineData("index")]
    public void HistoricalReconciliationRejectsForeignRecordedIdentityAtomically(string field)
    {
        var f = new Fixture();
        f.Plan(Request);
        var snapshot = f.Kernel.ExportSnapshot();
        var saved = snapshot.Goals[0];
        var gate = Assert.Single(saved.GateTestObligations!);
        var identity = gate.PlannerIdentity!;
        identity = field switch
        {
            "goal" => identity with { GoalId = GoalId.New().Value },
            "task" => identity with { RequestingTaskId = f.Reviewer.Id.Value },
            _ => identity with { CriterionIndex = 2 }
        };
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [saved with
        { CriterionEvidenceObligations = [], GateTestObligations = [gate with { PlannerIdentity = identity }] }] });
        Assert.Throws<InvalidOperationException>(() => f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "validate provenance"));
        Assert.Empty(f.Goal.CriterionEvidenceObligations);
    }

    [Fact]
    public void Reconciliation_AcceptedBatchRepairsAttestationPreservingHistory()
    {
        var f = new Fixture();
        f.Plan(Request);
        var snapshot = f.Kernel.ExportSnapshot();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = [snapshot.Goals[0] with { CriterionEvidenceObligations = [] }] });
        f.Review();
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
        var history = JsonSerializer.Serialize(f.Reviewer.VerificationHistory);
        var plannerHistory = JsonSerializer.Serialize(f.Planner.VerificationHistory);
        Assert.True(f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "project accepted current Planner round"));
        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        Assert.Equal(history, JsonSerializer.Serialize(f.Reviewer.VerificationHistory));
        Assert.Equal(plannerHistory, JsonSerializer.Serialize(f.Planner.VerificationHistory));
        Assert.Equal(Candidate, Assert.Single(f.Goal.GetLandingPrerequisiteObligations(Candidate)).ExpectedCandidateSha);
        Assert.False(f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "replay"));
    }

    [Fact]
    public void CurrentRoundReconciliationRetainsHistoricalProvenanceWithoutReauthorizingIt()
    {
        var f = new Fixture();
        f.Plan(Request);
        var historical = Assert.Single(Assert.Single(f.Goal.CriterionEvidenceObligations).PlannerSources!);
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "Current brief preserves the criterion while replacing Planner authority.");
        f.Kernel.RetryTask(f.Goal.Id, f.Planner.Id, "Plan the current brief.");
        f.Kernel.RecordTaskDispatch(f.Goal.Id, f.Planner.Id,
            new TaskDispatchRecord("fixture", "plan", "C:\\fixture", DateTimeOffset.UtcNow));
        f.Plan(Request);
        Assert.Equal(2, Assert.Single(f.Goal.CriterionEvidenceObligations).PlannerSources!.Count);
        var snapshot = f.Kernel.ExportSnapshot();
        var saved = snapshot.Goals[0];
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [saved with
        { CriterionEvidenceObligations = [saved.CriterionEvidenceObligations!.Single() with { PlannerSources = [historical] }] }] });

        Assert.True(f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "reconcile current directive only"));

        var sources = Assert.Single(f.Goal.CriterionEvidenceObligations).PlannerSources!;
        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, item => item.Identity == historical.Identity);
        Assert.Single(sources.Where(item => item.Identity.BriefVersion == f.Goal.AuthoritativeBrief.Version));
        Assert.False(f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "idempotent current directive replay"));
    }

    [Theory]
    [InlineData("assigned")]
    [InlineData("dispatched")]
    [InlineData("failed")]
    [InlineData("malformed")]
    [InlineData("directive-free")]
    public void SameBriefRetryRevokesHistoricalExecutionAuthority(string round)
    {
        var f = new Fixture();
        f.Plan(Request);
        var original = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.True(f.Goal.HasCurrentPlannerExecutionAuthority(original));
        Assert.Contains(AcceptanceGateOwnedHeading, f.Kernel.BuildTaskBrief(f.Goal.Id, f.Reviewer.Id).Content);
        f.Kernel.RetryTask(f.Goal.Id, f.Planner.Id, "Replace the accepted Planner round.");
        if (round != "assigned")
        {
            f.Kernel.RecordTaskDispatch(f.Goal.Id, f.Planner.Id,
                new TaskDispatchRecord("fixture", "plan", "C:\\fixture", DateTimeOffset.UtcNow));
            if (round != "dispatched")
                f.Plan(round == "malformed" ? "PLANNER_EVIDENCE_REQUEST: {bad}" :
                    round == "directive-free" ? "No execution directive in this round." : Request,
                    round == "failed" ? 1 : 0);
        }
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(f.Kernel.ExportSnapshot());
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "retain history without restoring authority");
        var retained = Assert.Single(f.Goal.CriterionEvidenceObligations);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(retained));
        Assert.False(f.Goal.HasCurrentPlannerExecutionAuthority(retained));
        Assert.DoesNotContain(AcceptanceGateOwnedHeading, f.Kernel.BuildTaskBrief(f.Goal.Id, f.Reviewer.Id).Content);
        f.Review();
        Assert.Equal(WorkTaskStatus.Failed, f.Reviewer.Status);
    }

    [Fact]
    public void AcceptedReplacementMustContainTheMatchingCriterionDirective()
    {
        var f = new Fixture(twoCriteria: true);
        f.Plan(Request);
        var original = Assert.Single(f.Goal.CriterionEvidenceObligations);
        f.Kernel.RetryTask(f.Goal.Id, f.Planner.Id, "Replace execution ownership for criterion two only.");
        f.Kernel.RecordTaskDispatch(f.Goal.Id, f.Planner.Id,
            new TaskDispatchRecord("fixture", "plan", "C:\\fixture", DateTimeOffset.UtcNow));
        f.Plan(Request.Replace("\"criterion_index\":1", "\"criterion_index\":2", StringComparison.Ordinal));
        Assert.Equal(WorkTaskStatus.Completed, f.Planner.Status);
        Assert.False(f.Goal.HasCurrentPlannerExecutionAuthority(original));
        Assert.True(f.Goal.HasCurrentPlannerExecutionAuthority(f.Goal.CriterionEvidenceObligations.Single(item => item.CriterionIndex == 1)));
        f.Kernel.RetryTask(f.Goal.Id, f.Planner.Id, "Restore the original directive in a fresh accepted round.");
        f.Kernel.RecordTaskDispatch(f.Goal.Id, f.Planner.Id,
            new TaskDispatchRecord("fixture", "plan", "C:\\fixture", DateTimeOffset.UtcNow));
        f.Plan(Request);
        Assert.True(f.Goal.HasCurrentPlannerExecutionAuthority(original));
        Assert.Single(f.Goal.CriterionEvidenceObligations.Single(item => item.CriterionIndex == 0).PlannerSources!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingOperatorMappingAfterReviewPreservesEvidenceAndIndependentAuthority(bool? passed)
    {
        var f = new Fixture();
        f.Plan(Request);
        f.Review();
        Assert.Equal(WorkTaskStatus.Completed, f.Reviewer.Status);
        var original = Assert.Single(f.Goal.CriterionEvidenceObligations);
        if (passed is { } result)
            f.Kernel.RecordCriterionEvidence(f.Goal.Id, original.Id, CriterionEvidenceOwner.Acceptance,
                Candidate, "actual-full-gate", original.RequiredScope, result, "Recorded full-gate result");
        original = Assert.Single(f.Goal.CriterionEvidenceObligations);
        var mapped = f.Kernel.MapCriterionEvidenceOwner(f.Goal.Id, 0, 1, original.Owner, "operator",
            original.RequiredScope, original.FindingStableId, original.ExpectedCandidateSha);
        Assert.Equal("operator mapping by operator", mapped.Provenance);
        Assert.Equal(Assert.Single(original.PlannerSources!) with { EstablishesOwnership = false }, Assert.Single(mapped.PlannerSources!));
        Assert.Equal(original.State, mapped.State);
        Assert.Equal(original.CandidateSha, mapped.CandidateSha);
        Assert.Equal(original.ReceiptId, mapped.ReceiptId);
        Assert.Equal(original.Detail, mapped.Detail);
        Assert.Equal(original.PriorReceipts, mapped.PriorReceipts);
        Assert.Equal(mapped, f.Kernel.MapCriterionEvidenceOwner(f.Goal.Id, 0, 1, mapped.Owner, "operator",
            mapped.RequiredScope, mapped.FindingStableId, mapped.ExpectedCandidateSha));
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "Revised brief keeps criteria and the explicit operator mapping.");
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(f.Kernel.ExportSnapshot());
        Assert.True(f.Goal.HasCurrentPlannerExecutionAuthority(Assert.Single(f.Goal.CriterionEvidenceObligations)));
        Assert.False(VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(f.Goal, 0));
    }

    [Theory]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    [InlineData(GoalStatus.Failed)]
    public void TerminalRecordsDoNotGainPlannerObligations(GoalStatus status)
    {
        var f = new Fixture();
        f.Plan(Request);
        var snapshot = f.Kernel.ExportSnapshot();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = [snapshot.Goals[0] with { CriterionEvidenceObligations = [], Status = status }] });
        var before = JsonSerializer.Serialize(f.Kernel.ExportSnapshot());
        f.Kernel.NormalizeGoalLifecycleState(f.Goal.Id, "terminal reconciliation");
        Assert.Equal(before, JsonSerializer.Serialize(f.Kernel.ExportSnapshot()));
    }

    private sealed class Fixture
    {
        internal AgentOrchestratorKernel Kernel { get; set; } = new();
        private readonly GoalId goalId;
        private readonly TaskId plannerId;
        private readonly TaskId reviewerId;
        internal Goal Goal => Kernel.GetGoal(goalId);
        internal TaskSpec Planner => Kernel.GetTask(goalId, plannerId);
        internal TaskSpec Reviewer => Kernel.GetTask(goalId, reviewerId);
        private int round;
        internal Fixture(bool twoCriteria = false)
        {
            var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
            var reviewer = new TaskSpec(TaskId.New(), "Review source safety and execution", AgentRole.Reviewer);
            plannerId = planner.Id;
            reviewerId = reviewer.Id;
            var goal = Kernel.CreateGoal("Preserve structured Planner ownership", [planner, reviewer]);
            goalId = goal.Id;
            Kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Preserve source review and defer execution only",
                twoCriteria ? [Criterion, "Observe independent behavior."] : [Criterion], VerificationClass.TestVerifiable, [], []));
            Kernel.ActivateGoal(goalId, DefaultAgents());
            Kernel.RecordTaskDispatch(goalId, plannerId, new TaskDispatchRecord("fixture", "plan", "C:\\fixture", DateTimeOffset.UtcNow));
        }
        internal void Plan(string batch, int exitCode = 0) => Kernel.RecordDispatchExecutionResult(goalId, plannerId,
            new TaskVerificationRecord("plan", "C:\\fixture", exitCode, batch + "\n" + Result, "",
                DateTimeOffset.UtcNow.AddTicks(++round), WorkerResultPresent: true, DispatchAttemptAt: Planner.LastDispatch!.DispatchedAt));
        internal void Review(string criterionVerdict = "not-verifiable", string verdict = "pass", string prefix = "", string findings = "[]")
        {
            Kernel.RecordTaskDispatch(goalId, reviewerId, new TaskDispatchRecord("fixture", "review", "C:\\fixture", DateTimeOffset.UtcNow));
            Kernel.RecordDispatchBaseCommit(goalId, reviewerId, Candidate);
            var output = prefix + "\n" + Result.Replace("END_WORKER_RESULT", $"findings: {findings}\ntouched_anchors: []\n" +
                $"criteria_verdicts: [{{\"criterion_index\":0,\"verdict\":\"{criterionVerdict}\",\"evidence\":\"Source inspected; Acceptance must execute regression checks\"}}]\n" +
                $"verdict: {verdict}\nEND_WORKER_RESULT", StringComparison.Ordinal);
            Kernel.RecordDispatchExecutionResult(goalId, reviewerId, new TaskVerificationRecord("review", "C:\\fixture", 0,
                output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true, ReviewedCommit: Candidate,
                DispatchAttemptAt: Reviewer.LastDispatch!.DispatchedAt));
        }
    }
}
