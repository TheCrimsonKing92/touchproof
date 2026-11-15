using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns its database, workspace and provider gates; CLI
// output uses the AsyncLocal console router, with no process or environment hooks.
public sealed class RefinementStaleBriefAttachTests : CliCommandTestBase
{
    private protected override bool UsesGit => false;

    private const string OriginalBrief = "Original held-refinement sentence.\n\n## Acceptance criteria\n\n1. Original criterion.";
    private const string RevisedBrief = "Revised held-refinement sentence.\n\n## Acceptance criteria\n\n1. Revised criterion.";
    private const string ProviderName = "stale-brief-regression-refiner";
    private const string OriginalResponse = """
        {"behavioralContract":"Original contract","acceptanceCriteria":[{"text":"Original criterion.","phase":"PreLanding","declared_index":1}],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
        """;
    private const string RevisedResponse = """
        {"behavioralContract":"Revised contract","acceptanceCriteria":[{"text":"Revised criterion.","phase":"PreLanding","declared_index":1}],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldRefinementIsRefusedAfterRevisionAndNextRunRefinesRevisedBrief(bool joined)
    {
        var scenario = await CreateScenarioAsync();
        var heldProvider = new RecordingProvider(OriginalResponse, hold: true);
        var processing = ProcessAsync(scenario, heldProvider);
        try
        {
            await AwaitEventAsync(heldProvider.Entered, "original-brief provider entry");
            Assert.NotNull(heldProvider.Request);
            Assert.Contains(heldProvider.Request.Messages,
                message => message.Content.Contains(OriginalBrief, StringComparison.Ordinal));
            Assert.Equal(OrchestratorStateOutboxStatus.Processing,
                (await scenario.Repository.GetOutboxStateAsync(scenario.MessageId))!.Status);

            await ReviseAsync(scenario, joined);

            Assert.Equal(RevisedBrief, scenario.Goal.AuthoritativeBrief.Text);
            Assert.Equal(2, scenario.Goal.AuthoritativeBrief.Version);
            Assert.Null(scenario.Goal.RefinedSpec);
            Assert.Empty(scenario.Goal.RefinedSpecVersions);
            Assert.Equal(OrchestratorStateOutboxStatus.Processing,
                (await scenario.Repository.GetOutboxStateAsync(scenario.MessageId))!.Status);
        }
        finally
        {
            heldProvider.Release();
            await AwaitEventAsync(processing, "held refinement completion");
        }

        var staleResult = await processing;
        Assert.True(staleResult.Claimed);
        Assert.False(staleResult.Attached);
        Assert.Equal(1, heldProvider.Calls);
        scenario.Kernel = await scenario.Repository.LoadAsync();
        Assert.Equal(2, scenario.Goal.AuthoritativeBrief.Version);
        Assert.Null(scenario.Goal.RefinedSpec);
        Assert.Empty(scenario.Goal.RefinedSpecVersions);
        Assert.True(GoalRefinementWorkCoordinator.HasPendingWork(scenario.Goal));
        var pending = await scenario.Repository.GetOutboxStateAsync(scenario.MessageId);
        Assert.NotNull(pending);
        Assert.Equal(OrchestratorStateOutboxStatus.Pending, pending.Status);
        Assert.Null(pending.Detail);
        Assert.Null(pending.ProcessingStartedAt);
        Assert.Equal(scenario.MessageId,
            Assert.Single(await scenario.Repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind)).Id);
        Assert.Equal(GoalRefinementReadiness.Pending,
            GoalRefinementGate.Evaluate(scenario.Goal, pending, new([])).Readiness);

        var revisedProvider = new RecordingProvider(RevisedResponse);
        var nextRun = ProcessAsync(scenario, revisedProvider);
        await AwaitEventAsync(nextRun, "revised-brief refinement completion");
        var result = await nextRun;
        Assert.True(result.Claimed);
        Assert.True(result.Attached);
        Assert.Equal(1, revisedProvider.Calls);
        Assert.NotNull(revisedProvider.Request);
        Assert.Contains(revisedProvider.Request.Messages,
            message => message.Content.Contains(RevisedBrief, StringComparison.Ordinal));
        Assert.DoesNotContain(revisedProvider.Request.Messages,
            message => message.Content.Contains("Original held-refinement sentence.", StringComparison.Ordinal));
        var refined = (await scenario.Repository.LoadAsync()).GetGoal(scenario.GoalId);
        Assert.Equal(["Revised criterion."], refined.RefinedSpec!.AcceptanceCriteria);
        Assert.Equal(2, Assert.Single(refined.RefinedSpecVersions).BriefVersion);
        Assert.Empty(await scenario.Repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
        Assert.Equal(GoalRefinementReadiness.Ready,
            GoalRefinementGate.Evaluate(refined, await scenario.Repository.GetOutboxStateAsync(scenario.MessageId), new([])).Readiness);
    }

    [Fact]
    public async Task RefinementWithoutRevisionAttachesForCurrentBrief()
    {
        var scenario = await CreateScenarioAsync();
        var provider = new RecordingProvider(OriginalResponse);
        var processing = ProcessAsync(scenario, provider);
        await AwaitEventAsync(processing, "unrevised refinement completion");

        var result = await processing;
        Assert.True(result.Claimed);
        Assert.True(result.Attached);
        Assert.Equal(1, provider.Calls);
        var refined = (await scenario.Repository.LoadAsync()).GetGoal(scenario.GoalId);
        Assert.Equal(1, refined.AuthoritativeBrief.Version);
        Assert.Equal(["Original criterion."], refined.RefinedSpec!.AcceptanceCriteria);
        Assert.Equal(1, Assert.Single(refined.RefinedSpecVersions).BriefVersion);
        Assert.Empty(await scenario.Repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
        Assert.Equal(GoalRefinementReadiness.Ready,
            GoalRefinementGate.Evaluate(refined, await scenario.Repository.GetOutboxStateAsync(scenario.MessageId), new([])).Readiness);
    }

    private async Task<Scenario> CreateScenarioAsync()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile(ProviderName, "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(OriginalBrief);
        GoalRefinementWorkCoordinator.RecordPending(kernel, goal.Id);
        await repository.SaveAsync(kernel);
        await repository.EnsureOutboxMessageAsync(GoalRefinementWorkCoordinator.CreateMessage(goal.Id));
        return new(workspace, repository, kernel, goal.Id);
    }

    private async Task ReviseAsync(Scenario scenario, bool joined)
    {
        if (joined)
        {
            await scenario.Repository.TransactGoalCreationWithOutboxAsync(
                GoalCreationLoadScope.ForGoals([scenario.GoalId], []), (stored, _) =>
                {
                    var result = ExecuteCliAndCaptureResult(["revise", scenario.GoalId.Value, RevisedBrief], stored, scenario.Workspace);
                    Assert.True(result.Changed);
                    return Task.FromResult((result.Changed, result.Output, (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                });
        }
        else
        {
            var result = ExecuteCliAndCaptureResult(["revise", scenario.GoalId.Value, RevisedBrief], scenario.Kernel, scenario.Workspace);
            Assert.True(result.Changed);
            await scenario.Repository.SaveAsync(scenario.Kernel);
        }
        scenario.Kernel = await scenario.Repository.LoadAsync();
    }

    private static Task<GoalRefinementWorkProcessResult> ProcessAsync(Scenario scenario, RecordingProvider provider) =>
        GoalRefinementWorkCoordinator.ProcessAsync(scenario.Repository, scenario.Workspace,
            new InMemoryModelProviderRegistry([provider]), WorkerProfileCatalog.Default(), scenario.GoalId);

    private static async Task AwaitEventAsync(Task task, string eventName)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Timed out waiting for {eventName}.", ex);
        }
    }

    private sealed class Scenario(OrchestratorWorkspace workspace, SqliteOrchestratorStateRepository repository,
        AgentOrchestratorKernel kernel, GoalId goalId)
    {
        public OrchestratorWorkspace Workspace { get; } = workspace;
        public SqliteOrchestratorStateRepository Repository { get; } = repository;
        public AgentOrchestratorKernel Kernel { get; set; } = kernel;
        public GoalId GoalId { get; } = goalId;
        public Goal Goal => Kernel.GetGoal(GoalId);
        public string MessageId => GoalRefinementWorkCoordinator.MessageId(GoalId);
    }

    private sealed class RecordingProvider(string response, bool hold = false) : IModelProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderName => RefinementStaleBriefAttachTests.ProviderName;
        public int Calls { get; private set; }
        public ModelRequest? Request { get; private set; }
        public Task Entered => _entered.Task;
        public void Release() => _released.TrySetResult();

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            _entered.TrySetResult();
            if (hold)
                await _released.Task.WaitAsync(cancellationToken);
            return new ModelResponse(response, new ModelUsage(1, 1), "stop");
        }
    }
}
