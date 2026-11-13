using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Uses an isolated SQLite workspace and the CLI environment collection for filing.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class GoalCitedDefectAdvisoryCliTests : CliCommandTestBase
{
    private protected override bool UsesGit => false;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Filing_PremiseCitation_SuggestsOnlyWhenCued(bool cued)
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var cited = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, AgentCatalog.Default().Agents, "Existing goal with a measured defect");
        Assert.False(cited.IsTerminal);
        Assert.Empty(cited.DependsOn);
        await repository.SaveAsync(kernel);

        var cited8 = cited.Id.Value[..8];
        var observation = cued
            ? $"Goal {cited8} ran three times."
            : $"Goal {cited8} supplied the measurement.";
        var brief = $"""
            Correct the cited defect in goal filing.

            ## Measured premise
            - {observation}

            ## What to build
            - Correct the filing defect.

            ## Acceptance criteria
            - The filing behavior is corrected.
            """;
        var briefPath = Path.Combine(workspace.ExecutionDirectory, "brief.md");
        File.WriteAllText(briefPath, brief);

        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var providers = new InMemoryModelProviderRegistry([new FixtureRefinerProvider()]);
        Goal? currentGoal = null;
        var stdout = "";
        var stderr = CaptureConsoleError(() =>
            stdout = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["goal", "--brief-file", briefPath], repository, workspace,
                ref agents, providers, ref profiles, ref currentGoal)));

        Assert.NotNull(currentGoal);
        Assert.NotEqual(cited.Id, currentGoal.Id);
        var restored = await repository.LoadAsync();
        Assert.Equal(2, restored.Goals.Count);
        var created = Assert.Single(restored.Goals, goal => goal.Id == currentGoal.Id);
        Assert.Equal(brief, created.Objective);
        Assert.Empty(created.DependsOn);
        Assert.Empty(Assert.Single(restored.Goals, goal => goal.Id == cited.Id).DependsOn);
        Assert.Contains(created.Id.Value[..8], stdout);
        Assert.DoesNotContain("Dependency suggestion advisory unavailable:", stderr);

        var suggestions = stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("DEPENDENCY_SUGGESTION", StringComparison.Ordinal)).ToArray();
        if (cued)
        {
            var new8 = created.Id.Value[..8];
            Assert.Equal($"DEPENDENCY_SUGGESTION goal={new8} cited={cited8} status={cited.Status} command=goal-depends {cited8} --on {new8}",
                Assert.Single(suggestions));
        }
        else
        {
            Assert.Empty(suggestions);
        }
    }

    private OrchestratorWorkspace CreateWorkspace()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile("fixture-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        return workspace;
    }

    private sealed class FixtureRefinerProvider : IModelProvider
    {
        public string ProviderName => "fixture-refiner";

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelResponse("""
                {"behavioralContract":"Correct the cited defect in goal filing.",
                 "acceptanceCriteria":["The filing behavior is corrected."],
                 "verificationClass":"TestVerifiable","decisions":[],"forks":[]}
                """, new ModelUsage(1, 1), "stop"));
    }
}
