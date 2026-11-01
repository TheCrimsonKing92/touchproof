using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsLandingDenylist;

public sealed class PassedCohortReceiptApprovalHoldTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void HeldTick_RequeuesPartnerWhileCapacityPreservesReceiptAndMembers()
    {
        using var scenario = new Scenario();
        using var capacity = Scenario.PauseNewGates();
        var beforeGoals = MemberState();
        var beforeReceipt = JsonSerializer.Serialize(scenario.Receipt);
        var decisions = CollaborationItemStore.ForDirectory(scenario.Workspace.OrchestratorDirectory);
        var beforeAttention = decisions.GetAttentionQueueAsync().GetAwaiter().GetResult();
        Assert.Empty(beforeAttention);
        var gateCount = scenario.Verifier.RunCount;
        for (var tick = 0; tick < 2; tick++)
        {
            var progress = scenario.Tick();
            Assert.Equal($"goal={scenario.Goals[0].Id.Value[..8]}", RecipientGoalField(
                Assert.Single(progress.Where(line => line.StartsWith("ADMISSION ", StringComparison.Ordinal) &&
                    line.Contains("reason=PassedCohortReceiptApprovalHeld", StringComparison.Ordinal)))));
            Assert.DoesNotContain(progress, line => line.Contains("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal) ||
                line.Contains("outcome=inflight", StringComparison.Ordinal) ||
                line.Contains("result=admitted", StringComparison.Ordinal));
            Assert.Equal(gateCount, scenario.Verifier.RunCount);
            scenario.AssertPartnerReleased(progress, heldIndex: 0);
            Assert.Equal(beforeGoals, MemberState());
            Assert.Equal(beforeReceipt, JsonSerializer.Serialize(scenario.Store.TryReadReceipt(scenario.Receipt.Identity.Value)));
            var approval = Assert.Single(decisions.GetAttentionQueueAsync().GetAwaiter().GetResult());
            Assert.Equal(CollaborationItemType.Decision, approval.Type);
            Assert.Equal(scenario.Goals[0].Id.Value, approval.GoalId);
            Assert.Contains($"approve-policy-change {scenario.Goals[0].Id.Value[..8]} {scenario.CandidateRevisions[0]}",
                approval.Body, StringComparison.Ordinal);
            Assert.All(scenario.Goals, goal =>
            {
                Assert.Empty(scenario.Store.ReadFinalizedLandingsForGoal(goal.Id));
                AssertNoLanded(scenario.Workspace, goal.Id);
                Assert.Equal(goal.Status, scenario.PersistedStatuses[goal.Id]);
            });
        }

        // The owner hold and the partner's independent capacity wait only change observations.
        string MemberState() => JsonSerializer.Serialize(scenario.Kernel.ExportSnapshot().Goals
            .Select(goal => goal with { Timeline = [], LandingBlocker = null }));
    }

    [Fact]
    public void ApprovalRecorded_NextTickLandsBothMembersFromSameReceiptWithoutNewGate()
    {
        using var scenario = new Scenario();
        using (Scenario.PauseNewGates())
        {
            scenario.AssertPartnerReleased(scenario.Tick(), heldIndex: 0);
        }
        var gateCount = scenario.Verifier.RunCount;
        var store = new SqliteOperatorIntentStore(Path.Combine(scenario.Workspace.OrchestratorDirectory,
            "operator-intents.db"), Path.Combine(scenario.Workspace.OrchestratorDirectory, "logs"));
        var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.ApprovePolicyChange, scenario.Goals[0].Id.Value, null,
            JsonSerializer.Serialize(new ApprovePolicyChangeOperatorIntentPayload(
                scenario.CandidateRevisions[0], "Owner reviewed protected content."), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            [], "owner", "cli", "local-process", DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Human);
        store.EnqueueAsync(intent).GetAwaiter().GetResult();
        var decisions = CollaborationItemStore.ForDirectory(scenario.Workspace.OrchestratorDirectory);
        var progress = scenario.Tick(new OperatorIntentCoordinator(store, decisions: decisions,
            goalStateVersionResolver: _ => 0));
        Assert.Equal(OperatorIntentStatus.Applied, store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
        Assert.Contains(progress, line => line.Contains(
            $"prelanded=true outcome=passed receipt={scenario.Receipt.ReceiptId}", StringComparison.Ordinal));
        Assert.Equal(gateCount, scenario.Verifier.RunCount);
        Assert.All(scenario.Goals, goal =>
        {
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Equal(GoalStatus.Completed, scenario.PersistedStatuses[goal.Id]);
            Assert.Equal(scenario.Receipt.ReceiptId, Assert.Single(scenario.Store.ReadFinalizedLandingsForGoal(goal.Id)).ReceiptId);
            using var landed = ReadLanded(scenario.Workspace, goal.Id);
            Assert.Equal("GoalLanded", landed.RootElement.GetProperty("eventType").GetString());
        });
    }

    [Fact]
    public void HeldTick_EmitsHoldReasonNamingWaitingMemberAndApprovalNotGate()
    {
        using var scenario = new Scenario();
        using var capacity = Scenario.PauseNewGates();
        var progress = scenario.Tick();
        var waiting = scenario.Goals[0].Id.Value[..8];
        var line = Assert.Single(progress.Where(line => line.StartsWith("ADMISSION ", StringComparison.Ordinal) &&
            line.Contains("reason=PassedCohortReceiptApprovalHeld", StringComparison.Ordinal)));
        Assert.Equal($"goal={waiting}", RecipientGoalField(line));
        Assert.Contains($"receipt={scenario.Receipt.ReceiptId}", line, StringComparison.Ordinal);
        Assert.Contains($"cohort waits on landing-denylist approval for {waiting}, not on a gate", line, StringComparison.Ordinal);
        Assert.Contains($"approve-policy-change {waiting} {scenario.CandidateRevisions[0]} --text-file <reason-file>", line, StringComparison.Ordinal);
        scenario.AssertPartnerReleased(progress, heldIndex: 0);
    }

    [Fact]
    public void PreWalkIntent_RequeuesPartnerThenRestoresApprovedReceiptForLanding()
    {
        using var scenario = new Scenario();
        var results = new Dictionary<string, ParallelLandingOutcome>();
        var progress = new List<string>();
        var second = scenario.Goals[1];
        var candidate = new ConductorSpeculativeAcceptanceCandidate(second.Id,
            scenario.Driver.ProjectGroupedAdmissionCandidate(second, ConductorAutonomyPolicy.Permissive, scenario.Kernel));
        var (eligible, candidates) = Apply();
        Assert.Equal(second.Id, Assert.Single(eligible).Id);
        Assert.Equal(candidate, Assert.Single(candidates));
        Assert.Equal(scenario.Goals[0].Id.Value, Assert.Single(results).Key);
        Assert.Null(ConductorAcceptanceCohortSelector.Select(candidates).Selection);
        Assert.Empty(scenario.Driver.FindLandablePassedCohortSelections(candidates));
        Assert.All(results.Values, result => Assert.True(PassedCohortReceiptApprovalHold.IsReadOnlyReservation(result.Result)));
        var heldGoal = scenario.Goals[0];
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(results[heldGoal.Id.Value].Result.Outcome);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("cohort-receipt-admission", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(1, decision.Rung);
        Assert.Equal("passed-cohort-awaiting-landing-denylist-approval", decision.DiscriminatingEvidence);
        Assert.Equal(held.Reason, decision.Reason);
        Assert.Contains($"approve-policy-change {scenario.Goals[0].Id.Value[..8]}", decision.Reason, StringComparison.Ordinal);
        Assert.Equal(new PolicyDecisionFact[]
        {
            new("goalId", heldGoal.Id.Value),
            new("receiptId", scenario.Receipt.ReceiptId),
            new("cohortIdentity", scenario.Receipt.Identity.Value),
            new("observedMainRevision", scenario.Receipt.Identity.ObservedMainRevision)
        }, decision.Facts);
        Assert.All(scenario.Goals, member =>
        {
            Assert.Equal(GoalStatus.Verified, member.Status);
            Assert.Empty(scenario.Store.ReadFinalizedLandingsForGoal(member.Id));
            AssertNoLanded(scenario.Workspace, member.Id);
        });
        Approve(scenario.Workspace, scenario.Goals[0], scenario.CandidateRevisions[0]);
        results.Clear();
        var (approvedEligible, approvedCandidates) = Apply();
        Assert.Equal(2, approvedEligible.Length);
        Assert.Equal(2, approvedCandidates.Length);
        Assert.Empty(results);
        scenario.Tick();
        Assert.All(scenario.Goals, goal => Assert.Equal(GoalStatus.Completed, goal.Status));
        Assert.Equal(1, scenario.Verifier.RunCount);

        (Goal[], ConductorSpeculativeAcceptanceCandidate[]) Apply() =>
            PassedCohortReceiptApprovalHoldAdmission.Apply(scenario.Driver, ConductorAutonomyPolicy.Permissive,
                scenario.Kernel, [second], [candidate], scenario.Goals, new HashSet<GoalId> { scenario.Goals[0].Id },
                _ => true, new HashSet<string>(), results, 1, progress.Add);
    }

    // EmitProgress appends a timestamp after the recipient. Ignore that suffix without matching
    // the waiting member's goal field embedded earlier in the shared approval reason.
    private static string? RecipientGoalField(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(field => field.StartsWith("goal=", StringComparison.Ordinal));

    internal sealed class Scenario : IDisposable
    {
        private readonly ManualResetEventSlim _started = new();
        private readonly ManualResetEventSlim _release = new();
        internal string Repo { get; } = CreateReducedAcceptanceCohortRepository();
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal[] Goals { get; }
        internal string[] CandidateRevisions { get; }
        internal OrchestratorWorkspace Workspace { get; }
        internal ConductorDriver Driver { get; }
        internal BlockingAcceptanceVerifier Verifier { get; }
        internal CohortAcceptanceStore Store { get; }
        internal AcceptanceCohortReceipt Receipt { get; }
        internal Dictionary<GoalId, GoalStatus> PersistedStatuses { get; } = [];

        internal Scenario(int? protectedMemberIndex = 0)
        {
            AddAcceptanceManifest(Repo);
            Goals = [CreateCompletedGoal(Kernel, "Protected member", Repo), CreateCompletedGoal(Kernel, "Ordinary member", Repo)];
            CandidateRevisions =
            [
                CreateWorktreeCandidate(Repo, Goals[0].Id,
                    protectedMemberIndex == 0 ? Protected : "src/Mcg.AgentOrchestrator.Infrastructure/ApprovalFirst.cs", "protected"),
                CreateWorktreeCandidate(Repo, Goals[1].Id,
                    protectedMemberIndex == 1 ? Protected : "tests/ApprovalPartner.cs", "ordinary")
            ];
            foreach (var (goal, revision) in Goals.Zip(CandidateRevisions))
            {
                Kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                Kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: revision);
            }
            Workspace = OrchestratorWorkspace.ForDirectory(Repo);
            Verifier = new BlockingAcceptanceVerifier(_started, _release,
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("approval-hold", true, 0, null)],
                    TestResultPaths: [WritePassingTrx(Repo, "approval-green.trx")]));
            Driver = new ConductorDriver(Kernel, Workspace, Verifier, AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(), runAcceptanceAttemptsInCurrentProcess: true,
                cleanupHooks: CreateIsolatedCleanupContext(Repo).Hooks);
            var selection = ProjectSelection(Driver, Goals[0], Goals[1]);
            var run = Driver.RunAcceptanceCohort(selection, Goals, ConductorAutonomyPolicy.Permissive, runGateInBackground: true);
            Assert.Contains("outcome=inflight", run.Detail, StringComparison.Ordinal);
            Assert.True(_started.Wait(TimeSpan.FromSeconds(15)), "The cohort gate did not signal its start.");
            _release.Set();
            Store = new CohortAcceptanceStore(Path.Combine(Workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            Assert.True(SpinWait.SpinUntil(() => Store.ReadPassedReceiptsForGoal(Goals[0].Id).Count == 1 &&
                Driver.GetActiveCohortGateMemberGoalIds().Count == 0, TimeSpan.FromSeconds(30)),
                "The cohort did not publish its passed receipt and release its gate reservation.");
            Receipt = Assert.Single(Store.ReadPassedReceiptsForGoal(Goals[0].Id));
            Assert.True(Receipt.HasAuthoritativeLandingEvidence);
            Assert.Equal(1, Verifier.RunCount);
        }

        internal string GoalState() => JsonSerializer.Serialize(Kernel.ExportSnapshot().Goals);

        // Receipt-replay tests observe requeue before a replacement gate changes a candidate.
        // The partner waits only for these unrelated slots, never for the owner's decision.
        internal static IDisposable PauseNewGates() => GateLoadContextProbe.PushLiveGateOccupantProbe(() =>
            Enumerable.Range(0, ConductorBatchLoop.MaxParallelAcceptanceCapacity)
                .Select(slot => new GateLoadContextProbe.LiveGateOccupant(null, $"approval-fixture-{slot}",
                    slot, TimeSpan.Zero, RunClass: GateHeartbeatRunClass.Acceptance,
                    RunId: $"approval-fixture-run-{slot}")).ToArray());

        internal void AssertPartnerReleased(IReadOnlyList<string> progress, int heldIndex)
        {
            var held = Goals[heldIndex];
            var partner = Goals[1 - heldIndex];
            Assert.Contains(progress, line => line.StartsWith("COHORT_RECEIPT_RELEASED ", StringComparison.Ordinal) &&
                line.Contains($"held={held.Id.Value[..8]} released={partner.Id.Value[..8]} path=requeue", StringComparison.Ordinal));
            Assert.DoesNotContain(progress, line => line.Contains("reason=PassedCohortReceiptApprovalHeld", StringComparison.Ordinal) &&
                RecipientGoalField(line) == $"goal={partner.Id.Value[..8]}");
            Assert.Contains(progress, line => line.StartsWith("ADMISSION ", StringComparison.Ordinal) &&
                line.Contains("reason=parallel-acceptance-slot-cap", StringComparison.Ordinal) &&
                line.Contains("deferred=1", StringComparison.Ordinal));
            var candidates = Goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(goal.Id,
                Driver.ProjectGroupedAdmissionCandidate(goal, ConductorAutonomyPolicy.Permissive, Kernel))).ToArray();
            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(candidates[heldIndex].ProjectionResult);
            Assert.IsType<GateReadyCandidateProjectionResult.Ready>(candidates[1 - heldIndex].ProjectionResult);
            Assert.Null(ConductorAcceptanceCohortSelector.Select(candidates).Selection);
            Assert.Empty(Driver.FindLandablePassedCohortSelections(candidates));
            Assert.All(Goals, goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.Empty(Store.ReadFinalizedLandingsForGoal(goal.Id));
                AssertNoLanded(Workspace, goal.Id);
            });
        }

        internal void BlockNextGate()
        {
            _started.Reset();
            _release.Reset();
        }

        internal void AssertGateStarted() =>
            Assert.True(_started.Wait(TimeSpan.FromSeconds(30)), "Ordinary admission did not start a new gate.");

        internal IReadOnlyList<string> Tick(OperatorIntentCoordinator? intents = null,
            ConductEventLogWriter? events = null)
        {
            BatchTickSummary? tick = null;
            new ConductorBatchLoop(operatorIntents: intents, conductEventLogWriter: events).Run(Kernel, Driver, ConductorAutonomyPolicy.Permissive,
                Path.Combine(Repo, "stop-does-not-exist"), maxIterations: 1, onTick: summary => tick = summary,
                persistGoalTick: (kernel, ids) =>
                {
                    foreach (var id in ids) PersistedStatuses[id] = kernel.GetGoal(id).Status;
                });
            Assert.NotNull(tick);
            return tick.ProgressLines;
        }

        public void Dispose()
        {
            _release.Set();
            var drained = SpinWait.SpinUntil(() => Driver.GetActiveCohortGateMemberGoalIds().Count == 0,
                TimeSpan.FromSeconds(30));
            Assert.True(drained, "The cohort gate did not drain before scenario teardown.");
            DeleteDirectory(Repo);
            _started.Dispose();
            _release.Dispose();
        }
    }
}
