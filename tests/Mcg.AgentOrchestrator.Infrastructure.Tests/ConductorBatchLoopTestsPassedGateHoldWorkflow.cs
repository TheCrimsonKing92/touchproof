using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Owns its kernel, SQLite stores and attempt artifacts. The collection isolates the
// gate census probe; gate execution, process liveness and landing are injected.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsPassedGateHoldWorkflow : ConductorBatchLoopTests
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Main = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public ConductorBatchLoopTestsPassedGateHoldWorkflow(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData("gate test obligation", "MissingTests", "reopen-regate", "retry", "goal-amend")]
    [InlineData("criterion evidence obligation", "operator observation", "criterion-evidence-record", "retry", "goal-amend")]
    [InlineData("landing denylist", "rule=policy path=config/policy.json", "approve-policy-change", null, null)]
    [InlineData("pending human wait", "request", "attention answer", null, null)]
    public async Task Passed_gate_enters_each_hold_updates_one_decision_and_clears_on_landing(
        string category, string rule, string verb, string? alternative, string? waiver)
    {
        using var fixture = new Fixture(category);
        Assert.Null(fixture.Coordinator.TryReadLatest(fixture.Goal.Id.Value));
        Assert.Empty(await fixture.Decisions.GetAttentionQueueAsync());

        fixture.Tick();

        Assert.Equal(1, fixture.GateRuns);
        Assert.Equal(0, fixture.Landings);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        var receipt = fixture.Latest;
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, receipt.Outcome);
        Assert.NotNull(receipt.ReconciledAt);
        var first = await fixture.Decision();
        Assert.Contains("passed gate, held: " + category, first.Body);
        Assert.Contains(rule, first.Body);
        Assert.Contains(receipt.AttemptId, first.Body);
        Assert.Contains(receipt.ResultPath, first.Body);
        Assert.Contains(Candidate, first.Body);
        Assert.Contains(verb, first.Body);
        if (alternative is not null) Assert.Contains(alternative, first.Body);
        if (waiver is not null) Assert.Contains(waiver, first.Body);
        if (fixture.Wait is { } wait) Assert.Contains(wait.Id.Value, first.Body);

        fixture.Tick();
        Assert.Equal(first.Id, (await fixture.Decision()).Id);
        Assert.Equal(1, fixture.GateRuns);

        fixture.ClearObligations();
        fixture.Category = category == "landing denylist" ? "gate test obligation" : "landing denylist";
        fixture.Tick();
        var changed = await fixture.Decision();
        Assert.Equal(first.Id, changed.Id);
        Assert.Contains("passed gate, held: " + fixture.Category, changed.Body);
        Assert.Contains(fixture.Category == "landing denylist" ? "approve-policy-change" : "reopen-regate", changed.Body);

        fixture.Category = null;
        fixture.Tick();
        Assert.Equal(1, fixture.Landings);
        Assert.Null(fixture.Goal.LandingBlocker);
        Assert.Empty(await fixture.Decisions.GetAttentionQueueAsync());
        Assert.Equal(0, fixture.WorkerDispatches);
    }

    [Fact]
    public async Task Leaving_verified_clears_the_active_decision_on_the_next_tick()
    {
        using var fixture = new Fixture("gate test obligation");
        fixture.Tick();
        await fixture.Decision();
        fixture.Kernel.CancelGoal(fixture.Goal.Id, "owner declined");
        fixture.Tick();
        Assert.Equal(GoalStatus.Cancelled, fixture.Goal.Status);
        Assert.Empty(await fixture.Decisions.GetAttentionQueueAsync());
    }

    [Fact]
    public async Task Verified_reopen_regate_runs_same_candidate_without_a_worker_round_and_audits_evidence()
    {
        using var fixture = new Fixture("gate test obligation");
        fixture.Tick();
        var prior = fixture.Latest;
        var task = fixture.Goal.Tasks.Single();
        var retryAt = task.LatestRetryAt;
        var dispatch = task.LastDispatch;
        var (coordinator, intent) = await fixture.Adjudication();

        var result = coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        coordinator.CompletePersisted([fixture.Goal.Id]);

        Assert.True(result.MutatedGoalState);
        Assert.False(result.RejectedAdjudication);
        Assert.NotNull(fixture.Latest.RegateRequestedAt);
        Assert.Contains("gate-request=queued", fixture.Latest.Detail!);
        Assert.Contains("MissingTests", fixture.Latest.Detail!);
        Assert.Contains(prior.AttemptId, fixture.Latest.Detail!);
        Assert.Contains(prior.ResultPath, fixture.Latest.Detail!);
        Assert.Contains(Candidate, fixture.Latest.Detail!);
        var decision = await fixture.Decisions.GetDecisionStateAsync("adjudicate-" + intent.Id);
        Assert.NotNull(decision?.Receipt);
        Assert.Equal(EffectReceiptStatus.Applied, decision!.Effect!.Status);
        Assert.Equal(2, decision.Receipt!.EvidenceHashes.Count); // operator evidence plus the held receipt

        fixture.Tick();
        Assert.Equal(2, fixture.GateRuns);
        Assert.NotEqual(prior.AttemptId, fixture.Latest.AttemptId);
        Assert.Equal(Candidate, fixture.Latest.BranchHeadSha);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, fixture.Latest.Outcome);
        Assert.Equal(retryAt, task.LatestRetryAt);
        Assert.Equal(dispatch, task.LastDispatch);
        Assert.Equal(0, fixture.WorkerDispatches);
    }

    [Theory]
    [InlineData(false, "goal-not-acceptance-failed")]
    [InlineData(true, "acceptance-attempt-live")]
    public async Task Verified_reopen_regate_refuses_never_run_or_live_attempt(bool live, string reason)
    {
        using var fixture = new Fixture("gate test obligation");
        if (live)
        {
            fixture.Tick();
            fixture.StartLiveAttempt();
            Assert.True(fixture.Coordinator.HasLiveAttempt(fixture.Goal.Id.Value));
        }
        else Assert.Null(fixture.Coordinator.TryReadLatest(fixture.Goal.Id.Value));
        var before = fixture.AttemptCount;
        var (coordinator, _) = await fixture.Adjudication();

        var result = coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        coordinator.CompletePersisted([fixture.Goal.Id]);

        Assert.True(result.RejectedAdjudication);
        Assert.Contains(result.ProgressLines, line => line.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(before, fixture.AttemptCount);
        Assert.Equal(live ? 1 : 0, fixture.GateRuns);
        Assert.Equal(0, fixture.RegateRequests);
        Assert.Equal(0, fixture.WorkerDispatches);
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("tool-home")]
    [InlineData("unchanged")]
    public void Manifest_comparison_on_next_tick_regates_only_changed_effective_inputs(string change)
    {
        using var fixture = new Fixture("gate test obligation");
        fixture.Tick();
        var prior = fixture.Latest;
        Assert.Equal(fixture.ManifestIdentity, prior.EffectiveManifestIdentity);
        fixture.ChangeManifest(change);
        Assert.Equal(change == "unchanged", fixture.ManifestIdentity == prior.EffectiveManifestIdentity);

        fixture.Tick();

        Assert.Equal(change == "unchanged" ? 1 : 2, fixture.GateRuns);
        Assert.Equal(change == "unchanged" ? 1 : 2, fixture.AttemptCount);
        if (change == "unchanged") Assert.Equal(prior.AttemptId, fixture.Latest.AttemptId);
        else
        {
            Assert.NotEqual(prior.AttemptId, fixture.Latest.AttemptId);
            Assert.Equal(fixture.ManifestIdentity, fixture.Latest.EffectiveManifestIdentity);
            Assert.Contains(GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries,
                entry => entry.Operation == "conductor:manifest-regate" &&
                    entry.Detail?.Contains(prior.AttemptId, StringComparison.Ordinal) == true);
        }
        Assert.Equal(Candidate, fixture.Latest.BranchHeadSha);
        Assert.Equal(0, fixture.Landings);
        Assert.Equal(0, fixture.WorkerDispatches);
        fixture.Tick();
        Assert.Equal(change == "unchanged" ? 1 : 2, fixture.GateRuns);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable _census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        private readonly string _attemptRoot;
        private readonly string _manifest;
        internal string Root { get; } = CreateTempDirectory("mcg-passed-hold-workflow");
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; }
        internal CollaborationItemStore Decisions { get; }
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
        internal ConductorDriver Driver { get; }
        internal string? Category { get; set; }
        internal HumanInputRequest? Wait { get; private set; }
        internal int GateRuns { get; private set; }
        internal int WorkerDispatches { get; private set; }
        internal int Landings { get; private set; }
        internal int RegateRequests { get; private set; }
        internal ConductorParallelAcceptanceAttempt Latest => Coordinator.TryReadLatest(Goal.Id.Value)!;
        internal int AttemptCount => Directory.Exists(Path.Combine(_attemptRoot, Goal.Id.Value))
            ? Directory.GetFiles(Path.Combine(_attemptRoot, Goal.Id.Value), "*.attempt.json").Length : 0;
        internal string ManifestIdentity => GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(Root, ["docs/workflow.md"]);

        internal Fixture(string category)
        {
            Category = category;
            _attemptRoot = Path.Combine(Root, "attempts");
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            _manifest = Path.Combine(Root, "config", "acceptance-manifest.json");
            File.WriteAllText(_manifest, """
                { "version": 1, "checks": [{ "name": "check", "type": "command", "command": "git", "arguments": ["diff", "--check"] }] }
                """);
            Goal = CreateVerifiedSimpleGoal(Kernel, "Passed gate workflow");
            var operatorEvidence = category == "criterion evidence obligation";
            var criterion = operatorEvidence ? "Owner observes behavior" : "Full gate passes";
            Kernel.RecordGoalRefinement(Goal.Id, new RefinedSpec(Goal.Objective,
                [criterion], operatorEvidence ? VerificationClass.RealWorldDependent : VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = operatorEvidence ? [] : [criterion] });
            Kernel.MapCriterionEvidenceOwner(Goal.Id, 0, 1,
                operatorEvidence ? CriterionEvidenceOwner.Operator : CriterionEvidenceOwner.Acceptance,
                "test", operatorEvidence ? "operator observation" : CriterionEvidenceScopes.FullAcceptanceGate,
                expectedCandidateSha: Candidate);
            Decisions = CollaborationItemStore.ForDirectory(Root);
            Coordinator = new(_attemptRoot, TrunkBranchName.Default, executionDirectory: Root,
                runInline: true, isProcessAlive: _ => true, acquireStableSlotLease: (_, _) => null);
            Driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: Landings > 0),
                dispatchAndStart: _ => { WorkerDispatches++; throw new InvalidOperationException("Unexpected worker dispatch"); },
                runAcceptanceWithSlot: (_, _) =>
                {
                    GateRuns++;
                    return new(true, [], BranchHeadSha: Candidate, MainHeadSha: Main);
                },
                land: Land,
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: _ => ["docs/workflow.md"],
                parallelAcceptanceAttemptCoordinator: Coordinator,
                resolveAcceptanceHeads: _ => (Candidate, Main), executionDirectory: Root);
            Driver.PassedGateHoldAttention = new(Decisions,
                goal => PassedGateRegateAdmission.Resolve(goal, Coordinator.TryReadLatest(goal.Id.Value), Candidate));
            Driver.RefreshHeldGateReceipts = goals => PassedReceiptManifestInvalidation.CheckAll(
                goals, Coordinator, Root, _ => Candidate);
        }

        internal void Tick()
        {
            Driver.BeginTick(Kernel, 0);
            new ConductorBatchLoop().Run(Kernel, Driver,
                ConductorAutonomyPolicy.Conservative with { AcceptanceWidth = 1 }, NoStopPath(),
                maxIterations: 1, onlyGoalId: Goal.Id.Value);
        }

        internal async Task<CollaborationItem> Decision()
        {
            var item = Assert.Single(await Decisions.GetAttentionQueueAsync());
            Assert.Equal(CollaborationItemType.Decision, item.Type);
            Assert.Equal(PassedGateHoldAttention.Prefix + Goal.Id.Value, item.CorrelationKey);
            return item;
        }

        internal void ClearObligations()
        {
            foreach (var obligation in Goal.GetOutstandingCriterionEvidenceObligations(Candidate))
                Kernel.RecordCriterionEvidence(Goal.Id, obligation.Id, CriterionEvidenceOwner.Operator,
                    Candidate, "operator-receipt", "operator observation", true, "confirmed");
            if (Wait is { } wait) Kernel.SubmitHumanInput(wait.Id, "confirmed", supersedeSiblings: false);
        }

        private LandingResult Land(Goal goal)
        {
            if (Category is null)
            {
                Landings++;
                return new(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Promote(),
                    TrunkBranchName.Default, true, "landed");
            }
            if (Category == "pending human wait" && Wait is null)
                Wait = Kernel.RequestHumanInputDeduplicated(goal.Id, null, "Owner supplies evidence",
                    kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: "operator").Request;
            var reason = Category switch
            {
                "gate test obligation" => $"acceptance passed at {Candidate[..8]}; gate test obligation 12345678 selector-not-found: MissingTests; source gate.result.json; closest: SkippedTests",
                "landing denylist" => "landing-denylist owner approval required: rule=policy path=config/policy.json",
                "pending human wait" => $"acceptance passed at {Candidate[..8]}; goal {goal.Id.Value} has pending human input: request {Wait!.Id.Value} (ProspectiveAcceptanceEvidence, owner operator)",
                _ => throw new InvalidOperationException("Criterion evidence should hold before landing")
            };
            var decision = new PolicyDecisionRecord("landing", "Escalate", 1, "acceptance-not-accepted", reason, []);
            return new(goal.Id.Value, goal.Id.Value[..8], new LandingDecision.Escalate(reason) { Decision = decision },
                TrunkBranchName.Default, false, "held");
        }

        internal void ChangeManifest(string change)
        {
            var manifest = JsonNode.Parse(File.ReadAllText(_manifest))!;
            if (change == "engine") manifest["engine"] = new JsonObject { ["maxConcurrentShards"] = 2 };
            if (change == "tool-home") manifest["toolHomes"] = new JsonArray(new JsonObject
                { ["variable"] = "CARGO_HOME", ["directory"] = Path.Combine(Root, "cargo") });
            File.WriteAllText(_manifest, manifest.ToJsonString(new() { WriteIndented = true }));
        }

        internal async Task<(OperatorIntentCoordinator, OperatorIntentRecord)> Adjudication()
        {
            var store = new SqliteOperatorIntentStore(Path.Combine(Root, "operator-intents.db"), Path.Combine(Root, "logs"));
            var coordinator = new OperatorIntentCoordinator(store, decisions: Decisions, goalStateVersionResolver: _ => 7,
                hasLiveAcceptanceAttempt: id => Coordinator.HasLiveAttempt(id.Value),
                passedGateHold: goal => PassedGateRegateAdmission.Resolve(goal, Coordinator.TryReadLatest(goal.Id.Value), Candidate),
                requestPassedGateRegate: (goal, hold, detail) =>
                {
                    RegateRequests++;
                    return Coordinator.TryInvalidatePassedReceipt(goal, hold.AttemptId, hold.Candidate,
                        ConductorEvidenceSupersessionCause.OperatorRegate, detail, () => Candidate);
                });
            var id = Guid.NewGuid().ToString("N");
            var payload = new AdjudicateOperatorIntentPayload("reopen-regate", "Cause fixed", ["receipt-1"], 7, Root);
            var intent = await store.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.Adjudicate,
                Goal.Id.Value, Goal.Tasks.Single().Id.Value, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [], "operator", "cli", "local-process", DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Human));
            return (coordinator, intent);
        }

        internal void StartLiveAttempt()
        {
            Assert.True(Coordinator.TryInvalidatePassedReceipt(Goal, Latest.AttemptId, Candidate,
                ConductorEvidenceSupersessionCause.OperatorRegate, "test live refusal", () => Candidate));
            var live = new ConductorParallelAcceptanceAttemptCoordinator(_attemptRoot, TrunkBranchName.Default,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(9876));
            var started = live.Evaluate(ConductorParallelAcceptanceCandidate.Create(Goal, 0,
                    ["docs/workflow.md"], Candidate, Main), ConductorAutonomyPolicy.Conservative,
                (_, _, _, _, _) => throw new InvalidOperationException("Synthetic process must not execute inline"));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
        }

        public void Dispose()
        {
            _census.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }
}
