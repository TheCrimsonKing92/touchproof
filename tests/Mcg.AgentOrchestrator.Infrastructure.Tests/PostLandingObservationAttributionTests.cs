using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its state, intent database, clock, and console capture.
public sealed class PostLandingObservationAttributionTests
{
    private const string Actor = "agent:observer@example";
    private static readonly string Candidate = new('a', 40);
    private static readonly DateTimeOffset PromotionTime = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly FixedClock Clock = new(PromotionTime.AddHours(1));

    [Fact]
    public async Task AgentRuntimeObservationPersistsAttribution()
    {
        using var fixture = new Fixture();
        var payload = Payload(fixture.Duty);
        var intent = await fixture.Enqueue(payload);

        var result = fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        Assert.True(result.MutatedGoalState);
        var goal = await fixture.PersistAndReload();

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(intent.Id))!.Status);
        var duty = goal.CriterionEvidenceObligations.Single(item => item.Id == payload.ObligationId);
        Assert.Equal(CriterionEvidenceState.Satisfied, duty.State);
        Assert.Equal(OperatorActorKind.Agent, duty.ObservationActorKind);
        Assert.Equal(intent.Actor, duty.ObservationActor);
        Assert.Equal(payload.Observation, duty.Observation);
        Assert.DoesNotContain(goal.OutstandingPostLandingDuties, item => item.Id == duty.Id);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("promoted-revision")]
    [InlineData("chronology")]
    [InlineData("runtime-attestation")]
    public async Task InvalidAgentObservationLeavesDutyPending(string invalidField)
    {
        using var fixture = new Fixture();
        var payload = Payload(fixture.Duty);
        var observation = payload.Observation!;
        var invalid = invalidField switch
        {
            "scope" => observation with { Scope = observation.Scope with { Target = "another-runtime" } },
            "promoted-revision" => observation with { PromotedMainRevision = Candidate },
            "chronology" => observation with { ObservedAt = observation.ActivatedAt.AddSeconds(-1) },
            "runtime-attestation" => observation with { ActivationAttested = false },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField))
        };
        var intent = await fixture.Enqueue(payload with { Observation = invalid });

        var result = fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal);

        Assert.False(result.MutatedGoalState);
        var rejected = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
        Assert.Contains("requires attributed activation and a subsequent matching observation", rejected.Outcome,
            StringComparison.Ordinal);
        var duty = fixture.Goal.OutstandingPostLandingDuties.Single(item => item.Id == payload.ObligationId);
        Assert.Equal(CriterionEvidenceState.Pending, duty.State);
        Assert.Null(duty.Observation);
        Assert.Null(duty.ObservationActorKind);
    }

    [Fact]
    public void LegacyObservationRoundTripsAndDisplaysHuman()
    {
        var (kernel, goal) = CreateCompletedGoal();
        var duty = goal.CriterionEvidenceObligations[0];
        var payload = Payload(duty);
        kernel.RecordCriterionEvidence(goal.Id, duty.Id, duty.Owner, Candidate, "failed-receipt", duty.RequiredScope,
            false, "Condition initially absent", payload.Observation, Actor, OperatorActorKind.Human);
        kernel.RecordCriterionEvidence(goal.Id, duty.Id, duty.Owner, Candidate, payload.ReceiptId, duty.RequiredScope,
            true, payload.Detail, payload.Observation, Actor, OperatorActorKind.Human);
        var legacySnapshot = JsonSerializer.SerializeToNode(kernel.ExportSnapshot())!;
        var legacyDuty = legacySnapshot["Goals"]![0]!["CriterionEvidenceObligations"]![0]!.AsObject();
        Assert.True(legacyDuty.Remove("ObservationActorKind"));
        Assert.True(legacyDuty["PriorReceipts"]![0]!.AsObject().Remove("ObservationActorKind"));

        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(legacySnapshot.Deserialize<OrchestratorSnapshot>()!, Clock);
        var restored = restoredKernel.GetGoal(goal.Id);
        var exported = JsonSerializer.SerializeToNode(restoredKernel.ExportSnapshot())!;
        var exportedDuty = exported["Goals"]![0]!["CriterionEvidenceObligations"]![0]!.AsObject();

        Assert.Equal(CriterionEvidenceState.Satisfied, restored.CriterionEvidenceObligations[0].State);
        Assert.False(exportedDuty.ContainsKey("ObservationActorKind"));
        Assert.False(exportedDuty["PriorReceipts"]![0]!.AsObject().ContainsKey("ObservationActorKind"));
        Assert.True(JsonNode.DeepEquals(legacyDuty, exportedDuty), $"Round-tripped obligation: {exportedDuty}");
        var output = AsyncLocalConsoleRouter.Capture(() => GoalTextView.PrintGoal(restored));
        Assert.EndsWith($" observed-by=human:{Actor}", DutyLine(output, duty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoalStatusDisplaysAgentOnObservedDuty()
    {
        using var fixture = new Fixture();
        await fixture.Enqueue(Payload(fixture.Duty));
        fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        var goal = await fixture.PersistAndReload();

        var output = AsyncLocalConsoleRouter.Capture(() => GoalTextView.PrintGoal(goal));

        Assert.EndsWith($" observed-by=agent:{Actor}", DutyLine(output, fixture.Duty), StringComparison.Ordinal);
        Assert.DoesNotContain("observed-by=", DutyLine(output, Assert.Single(goal.OutstandingPostLandingDuties)),
            StringComparison.Ordinal);
    }

    private static string DutyLine(string output, CriterionEvidenceObligation duty) =>
        Assert.Single(output.Split('\n').Select(line => line.TrimEnd('\r')),
            line => line.StartsWith($"  - {duty.DisplayLabel}:", StringComparison.Ordinal));

    private static CriterionEvidenceReceiptOperatorIntentPayload Payload(CriterionEvidenceObligation duty) =>
        new(duty.Id, CriterionEvidenceOwner.Operator, Candidate, "observation-receipt", duty.RequiredScope, true,
            "Observed the activated behavior", new CriterionPostLandingObservation(duty.Publication!.PromotedMainRevision,
                duty.ObservationScope!, "runtime-instance-1", "Verified the running revision and activation record",
                PromotionTime.AddMinutes(1), PromotionTime.AddMinutes(2), ActivationAttested: true,
                GoalId: duty.Publication.GoalId, ObligationId: duty.Id));

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateCompletedGoal()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock(PromotionTime));
        var goal = kernel.CreateGoal("Observe behavior after publication");
        string[] criteria = ["Observe the first runtime condition", "Observe the second runtime condition"];
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Observe runtime", criteria,
            VerificationClass.RealWorldDependent, [], [])
        {
            OperatorOwnedAcceptanceCriteria = criteria,
            CriterionEvidenceDeclarations = criteria.Select(criterion => new CriterionEvidenceDeclaration(criterion,
                CriterionEvidencePhase.PostLanding, new CriterionObservationScope(CriterionObservationKind.Runtime,
                    criterion, "local-runtime"), Atomic: true)).ToArray()
        });
        kernel.BindPostLandingPublication(goal.Id, new GoalPromotionFact(goal.Id.Value, Candidate, new string('b', 40),
            new string('c', 40), PromotionTime, GoalPromotionFact.SoloProvenance));

        var landed = kernel.ExportGoalSnapshot(goal.Id) with { Status = GoalStatus.Completed };
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([landed], []), Clock);
        return (restored, restored.GetGoal(goal.Id));
    }

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("observation-attribution-").FullName;
        private readonly SqliteOrchestratorStateRepository _repository;

        public Fixture()
        {
            (Kernel, Goal) = CreateCompletedGoal();
            Duty = Goal.CriterionEvidenceObligations[0];
            var workspace = OrchestratorWorkspace.ForDirectory(_root);
            _repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
            Store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            Coordinator = new OperatorIntentCoordinator(Store, utcNow: () => Clock.UtcNow);
        }

        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public CriterionEvidenceObligation Duty { get; }
        public SqliteOperatorIntentStore Store { get; }
        public OperatorIntentCoordinator Coordinator { get; }

        public Task<OperatorIntentRecord> Enqueue(CriterionEvidenceReceiptOperatorIntentPayload payload) =>
            Store.EnqueueAsync(new OperatorIntentRecord("observation-intent", "observation-intent",
                OperatorIntentVerbs.CriterionEvidenceRecord, Goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], Actor, "cli", "local-process",
                Clock.UtcNow, ActorKind: OperatorActorKind.Agent));

        public async Task<Goal> PersistAndReload()
        {
            await _repository.SaveAsync(Kernel);
            Coordinator.CompletePersisted([Goal.Id]);
            return (await _repository.LoadGoalsAsync([Goal.Id])).GetGoal(Goal.Id);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
