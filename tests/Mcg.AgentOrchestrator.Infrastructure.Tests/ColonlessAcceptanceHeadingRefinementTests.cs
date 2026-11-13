using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static InfrastructureTestSupport;

// Parallel-safe: each test owns its workspace and stores; LaunchOverride is flow-local.
public sealed class ColonlessAcceptanceHeadingRefinementTests
{
    private const string GateCriterion =
        "The preview shows the declared items in order. Developer owns; Acceptance executes. TEST-VERIFIABLE.";
    private const string WorkerCriterion =
        "The diff preserves ownership checks. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.";
    private const string OperatorCriterion =
        "The operator observes the preview after activation. Operator owns; Operator executes. REAL-WORLD-DEPENDENT.";
    private static readonly string[] Criteria = [GateCriterion, WorkerCriterion, OperatorCriterion];
    private static readonly string[] Notes =
        ["Trace the Planner.", "Use the configured Reviewer.", "Keep the parent backlog item open."];
    private static string Objective => "Implement the preview.\n\nAcceptance criteria\n" +
        string.Join('\n', Criteria.Select((text, index) => $"{index + 1}. {text}")) +
        "\n\nOperating instructions\n" + string.Join('\n', Notes.Select(text => "- " + text));

    [Theory(DisplayName = "Served intake retains three declared criteria when six are proposed")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServedIntakeKeepsThreeCriteriaAndNoOperatingNoteObligations(bool operatorNotes)
    {
        using var scenario = await Scenario.CreateAsync(Objective, SixProposedCriteria(operatorNotes));
        Assert.Null(scenario.Goal.RefinedSpec);
        Assert.Equal(Objective, scenario.Goal.Objective);
        Assert.NotNull(await scenario.Repository.GetOutboxStateAsync(
            GoalRefinementWorkCoordinator.MessageId(scenario.Goal.Id)));

        var result = await scenario.RefineAsync();

        Assert.True(result.Claimed);
        Assert.True(result.Attached);
        Assert.NotNull(scenario.Provider.LastRequest);
        var spec = Assert.IsType<RefinedSpec>(scenario.Goal.RefinedSpec);
        Assert.Equal(Criteria, spec.AcceptanceCriteria);
        Assert.Equal([GateCriterion], spec.AcceptanceGateOwnedAcceptanceCriteria);
        Assert.Equal([OperatorCriterion], spec.OperatorOwnedAcceptanceCriteria);
        Assert.Equal(Objective, scenario.Goal.Objective);
        Assert.Equal(2, scenario.Goal.CriterionEvidenceObligations.Count);
        Assert.All(scenario.Goal.CriterionEvidenceObligations,
            obligation => Assert.Contains(obligation.CriterionIndex, new[] { 0, 2 }));
        Assert.DoesNotContain(spec.AcceptanceCriteria, criterion => Notes.Contains(criterion));
        var clarifications = await scenario.Collaboration.ListAsync(scenario.Goal.Id.Value);
        if (operatorNotes)
        {
            Assert.NotEmpty(spec.OpenQuestions);
            Assert.All(spec.OpenQuestions, question => Assert.Contains("has no unique declared criterion", question.Question));
            Assert.NotEmpty(clarifications);
        }
        else
        {
            Assert.Empty(spec.OpenQuestions);
            Assert.Empty(clarifications);
        }
    }

    [Fact(DisplayName = "Reload retains exact criterion identities and a pending later operator duty")]
    public async Task ReloadPreservesOrderOwnershipAndDeferredOperatorDuty()
    {
        using var scenario = await Scenario.CreateAsync(Objective, SixProposedCriteria(false));
        await scenario.RefineAsync();
        var before = scenario.Goal;
        Assert.Equal(Criteria, before.RefinedSpec!.AcceptanceCriteria);
        var obligationIdentities = before.CriterionEvidenceObligations
            .Select(item => (item.Id, item.CriterionIndex, item.CriterionVersion, item.Owner, item.Phase)).ToArray();
        await scenario.Repository.SaveAsync(scenario.Kernel);

        var restored = (await scenario.Repository.LoadAsync()).GetGoal(before.Id);

        Assert.Equal(Criteria, restored.RefinedSpec!.AcceptanceCriteria);
        Assert.Equal([GateCriterion], restored.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Assert.Equal([OperatorCriterion], restored.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        Assert.Equal(obligationIdentities, restored.CriterionEvidenceObligations
            .Select(item => (item.Id, item.CriterionIndex, item.CriterionVersion, item.Owner, item.Phase)).ToArray());
        var duty = Assert.Single(restored.OutstandingPostLandingDuties);
        Assert.Equal(2, duty.CriterionIndex);
        Assert.Equal(CriterionEvidenceOwner.Operator, duty.Owner);
        Assert.Equal(CriterionEvidencePhase.PostLanding, duty.Phase);
        Assert.Equal(CriterionEvidenceState.Pending, duty.State);
        Assert.Equal(OperatorCriterion, duty.Criterion);
        Assert.True(duty.IsValidatedPostLanding);
        Assert.Equal(new CriterionObservationScope(CriterionObservationKind.Runtime,
            "Preview activated", "local-runtime"), duty.ObservationScope);
        Assert.DoesNotContain(restored.GetLandingPrerequisiteObligations("candidate"), item => item.Id == duty.Id);
        Assert.Equal(CriterionEvidenceOwner.Acceptance,
            Assert.Single(restored.GetLandingPrerequisiteObligations("candidate")).Owner);
    }

    [Theory(DisplayName = "Empty and malformed explicit sections leave an actionable intake receipt")]
    [InlineData("", false, "empty-section")]
    [InlineData("Unlisted prose.", false, "malformed-section")]
    [InlineData("", true, "empty-section")]
    [InlineData("1.No separator", true, "malformed-section")]
    public async Task ExplicitSectionDiagnosticSurvivesValidAndFallbackRefinement(
        string body, bool invalidOutput, string kind)
    {
        var objective = $"Build.\n\nAcceptance criteria\n{body}\nOperating instructions\n- Keep context.";
        using var scenario = await Scenario.CreateAsync(objective, invalidOutput ? "invalid output" :
            Response([new { text = WorkerCriterion, evidence_owner = "worker" }]));

        await scenario.RefineAsync();

        Assert.NotNull(scenario.Provider.LastRequest);
        Assert.NotNull(scenario.Goal.RefinedSpec);
        var receipt = Assert.Single(scenario.Goal.Timeline.Where(entry =>
            entry.Kind == ProgressKind.GoalPolicyDecision &&
            entry.Message.StartsWith("Spec refiner parse diagnostic: Acceptance criteria", StringComparison.Ordinal)));
        Assert.Equal($"Spec refiner parse diagnostic: Acceptance criteria {kind} at line 3 (Acceptance criteria): " +
            "add numbered or bulleted items directly under the heading, or remove the heading.", receipt.Message);
        if (!invalidOutput)
            Assert.Equal([WorkerCriterion], scenario.Goal.RefinedSpec.AcceptanceCriteria);
    }

    [Fact(DisplayName = "Freeform objectives still use model criteria without an intake diagnostic")]
    public async Task FreeformRefinementRemainsUnchanged()
    {
        using var scenario = await Scenario.CreateAsync("Build the preview without a declared list.",
            Response([new { text = WorkerCriterion, evidence_owner = "worker" }]));

        await scenario.RefineAsync();

        Assert.NotNull(scenario.Provider.LastRequest);
        Assert.Equal([WorkerCriterion], scenario.Goal.RefinedSpec!.AcceptanceCriteria);
        Assert.Empty(scenario.Goal.RefinedSpec.OpenQuestions);
        Assert.DoesNotContain(scenario.Goal.Timeline,
            entry => entry.Message.StartsWith("Spec refiner parse diagnostic: Acceptance criteria", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Owner revision replaces the declared colonless list in its new order")]
    public async Task DeliberateBriefRevisionReplacesDeclaredList()
    {
        using var scenario = await Scenario.CreateAsync(Objective, SixProposedCriteria(false));
        await scenario.RefineAsync();
        Assert.Equal(Criteria, scenario.Goal.RefinedSpec!.AcceptanceCriteria);

        // Retain the later duty and its declaration while replacing the gate criterion.
        scenario.Kernel.ReviseGoalBrief(scenario.Goal.Id,
            $"Revise the preview.\nAcceptance criteria\n1. {OperatorCriterion}\n2. {WorkerCriterion}\n" +
            "Operating instructions\n- Keep the parent open.");

        Assert.Equal(2, scenario.Goal.AuthoritativeBrief.Version);
        Assert.Equal([OperatorCriterion, WorkerCriterion], scenario.Goal.RefinedSpec!.AcceptanceCriteria);
        Assert.Empty(scenario.Goal.RefinedSpec.AcceptanceGateOwnedAcceptanceCriteria);
        Assert.Equal([OperatorCriterion], scenario.Goal.RefinedSpec.OperatorOwnedAcceptanceCriteria);
        var duty = Assert.Single(scenario.Goal.CriterionEvidenceObligations,
            item => item.CriterionVersion == scenario.Goal.AuthoritativeRefinedSpecVersion!.Version);
        Assert.Equal(0, duty.CriterionIndex);
        Assert.Equal(OperatorCriterion, duty.Criterion);
        Assert.Equal(CriterionEvidenceOwner.Operator, duty.Owner);
        Assert.Equal(CriterionEvidencePhase.PostLanding, duty.Phase);
        Assert.Equal(CriterionEvidenceState.Pending, duty.State);
        Assert.True(duty.IsValidatedPostLanding);
    }

    [Theory(DisplayName = "Colonless removal declarations remain outside ordinary criteria")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalDeclarationsKeepTheirPartitionWithoutDiagnostic(bool allRemovals)
    {
        const string removal = "test-removal: Example.Tests.Removed";
        var objective = "Build.\nAcceptance criteria\n- " + removal +
            (allRemovals ? "" : "\n- " + WorkerCriterion) +
            "\nOperating instructions\n- Keep context.";
        using var scenario = await Scenario.CreateAsync(objective,
            Response(allRemovals ? [new { text = removal, declared_index = 1 }] :
                new object[] { new { text = removal, declared_index = 1 },
                    new { text = WorkerCriterion, declared_index = 2 } }));

        await scenario.RefineAsync();

        var partition = DeclaredTestRemovalSet.FromObjective(scenario.Goal.Objective);
        Assert.Equal("Example.Tests.Removed", Assert.Single(partition.Declarations).TestIdentity);
        Assert.Equal(allRemovals ? Array.Empty<string>() : [WorkerCriterion], partition.Ordinary);
        Assert.Equal(partition.Ordinary, scenario.Goal.RefinedSpec!.AcceptanceCriteria);
        Assert.Empty(scenario.Goal.RefinedSpec.OpenQuestions);
        Assert.DoesNotContain(scenario.Goal.Timeline,
            entry => entry.Message.StartsWith("Spec refiner parse diagnostic: Acceptance criteria", StringComparison.Ordinal));
    }

    private static string SixProposedCriteria(bool operatorNotes) => Response(
        new object[]
        {
            new { text = GateCriterion, declared_index = 1, evidence_owner = "worker" },
            new { text = WorkerCriterion, declared_index = 2, evidence_owner = "acceptance-gate" },
            new { text = OperatorCriterion, declared_index = 3, evidence_owner = "operator",
                phase = "PostLanding", atomic = true,
                observation_scope = new { kind = "Runtime", condition = "Preview activated", target = "local-runtime" } }
        }.Concat(Notes.Select(text => (object)new { text, evidence_owner = operatorNotes ? "operator" : "worker" })));

    private static string Response(IEnumerable<object> criteria) => JsonSerializer.Serialize(new
    {
        behavioralContract = "Implement the preview.", acceptanceCriteria = criteria,
        verificationClass = "TestVerifiable", decisions = Array.Empty<object>(), forks = Array.Empty<object>()
    });

    private sealed class Scenario : IDisposable
    {
        public OrchestratorWorkspace Workspace { get; }
        public SqliteOrchestratorStateRepository Repository { get; }
        public FakeSmokeProvider Provider { get; }
        public IModelProviderRegistry Providers { get; }
        public AgentOrchestratorKernel Kernel { get; private set; }
        public Goal Goal => Kernel.GetGoal(_goalId);
        public CollaborationItemStore Collaboration { get; }
        private readonly GoalId _goalId;

        private Scenario(OrchestratorWorkspace workspace, SqliteOrchestratorStateRepository repository,
            FakeSmokeProvider provider, AgentOrchestratorKernel kernel, GoalId goalId)
        {
            Workspace = workspace;
            Repository = repository;
            Provider = provider;
            Providers = new InMemoryModelProviderRegistry([provider]);
            Kernel = kernel;
            _goalId = goalId;
            Collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        }

        public static async Task<Scenario> CreateAsync(string objective, string response)
        {
            var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
                new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                    new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))]));
            var provider = new FakeSmokeProvider(response, providerName: "fake-refiner");
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([provider]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            GoalId? launchedGoal = null;
            var previous = GoalRefinementWorkCoordinator.LaunchOverride;
            GoalRefinementWorkCoordinator.LaunchOverride = (_, goalId) =>
            {
                launchedGoal = goalId;
                return new GoalRefinementWorkLaunchResult(false, null, "test-launch-suppressed");
            };
            var briefPath = Path.Combine(workspace.RootDirectory, "brief.md");
            await File.WriteAllTextAsync(briefPath, objective);
            try
            {
                CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                    ["goal", "--brief-file", briefPath], repository, workspace, ref agents,
                    providers, ref profiles, ref currentGoal));
            }
            finally
            {
                GoalRefinementWorkCoordinator.LaunchOverride = previous;
                File.Delete(briefPath);
            }
            var kernel = await repository.LoadAsync();
            var goal = Assert.Single(kernel.Goals);
            Assert.Equal(goal.Id, launchedGoal);
            return new(workspace, repository, provider, kernel, goal.Id);
        }

        public async Task<GoalRefinementWorkProcessResult> RefineAsync()
        {
            var result = await GoalRefinementWorkCoordinator.ProcessAsync(
                Repository, Workspace, Providers, WorkerProfileCatalog.Default(), _goalId);
            Assert.True(result.Claimed);
            Assert.True(result.Attached);
            Kernel = await Repository.LoadAsync();
            return result;
        }

        public void Dispose() => Directory.Delete(Workspace.RootDirectory, recursive: true);
    }
}
