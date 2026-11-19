using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static InfrastructureTestSupport;

// Parallel-safe: each case owns its SQLite store, workspace, and provider signals.
public sealed class RefinedSpecSingleStorageConsumerTests
{
    private static readonly JsonSerializerOptions StoreOptions = SqliteOrchestratorStateRepository.HistorySerializerOptions;

    [Fact]
    public void ThreeVersionsRoundTripAndSaveExactlyOneSpecValueLessTheNullLiteral()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Process requests using the specified policy.");
        kernel.RecordGoalRefinement(goal.Id, CreateSpec("Queue requests."));
        kernel.ReviseGoalBrief(goal.Id, "Process requests in priority order.");
        RecordLaterVersions(kernel, goal.Id);

        var exported = kernel.ExportGoalSnapshot(goal.Id);
        Assert.Equal(new[] { 1, 2, 3 }, goal.RefinedSpecVersions.Select(version => version.Version));
        Assert.Equal(new[] { 1, 2, 2 }, goal.RefinedSpecVersions.Select(version => version.BriefVersion));
        var currentSpec = exported.RefinedSpecVersions![2].Spec;
        var serializer = new GoalTaskHistoryStore(StoreOptions);
        var document = serializer.SerializeDocument(exported);
        var duplicated = serializer.SerializeDocument(exported with { RefinedSpec = currentSpec });

        var oneSpecBytes = JsonSerializer.SerializeToUtf8Bytes(currentSpec, StoreOptions).Length;
        Assert.Equal(oneSpecBytes - 4, Encoding.UTF8.GetByteCount(duplicated) - Encoding.UTF8.GetByteCount(document));

        var restoredKernel = new AgentOrchestratorKernel();
        restoredKernel.ReplaceGoalWithSnapshot(Assert.IsType<GoalSnapshot>(serializer.DecodeDocument(document, new())));
        var restored = restoredKernel.GetGoal(goal.Id);
        Assert.Equal(JsonSerializer.Serialize(goal.RefinedSpec, StoreOptions),
            JsonSerializer.Serialize(restored.RefinedSpec, StoreOptions));
        Assert.Equal(JsonSerializer.Serialize(goal.RefinedSpecVersions, StoreOptions),
            JsonSerializer.Serialize(restored.RefinedSpecVersions, StoreOptions));
    }

    [Fact]
    public async Task DispatchHydratesHigherVersionFromVersionOnlyStoredSpec()
    {
        using var store = new TestStore();
        var kernel = new AgentOrchestratorKernel();
        var stale = kernel.CreateGoal("Process requests.");
        kernel.RecordGoalRefinement(stale.Id, CreateSpec("Queue requests."));
        var storedKernel = new AgentOrchestratorKernel();
        storedKernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(stale.Id));
        RecordLaterVersions(storedKernel, stale.Id);
        var expected = storedKernel.ExportGoalSnapshot(stale.Id) with { RefinedSpec = null };
        await store.Repository.SaveGoalSnapshotsAsync([expected]);
        await AssertVersionOnlyRowAsync(store.Repository, stale.Id, expected);
        Assert.Equal(1, stale.AuthoritativeRefinedSpecVersion!.Version);

        GoalDispatchOperations.EnsureRefinedForSpecConsumer(
            kernel, store.Workspace, new InMemoryModelProviderRegistry([]), stale);

        var hydrated = kernel.GetGoal(stale.Id);
        Assert.NotSame(stale, hydrated);
        Assert.Equal(3, hydrated.AuthoritativeRefinedSpecVersion!.Version);
        Assert.Equal(expected.RefinedSpecVersions![2].Spec.BehavioralContract, hydrated.RefinedSpec!.BehavioralContract);
    }

    [Fact]
    public async Task FirstTimeRefinementLeavesConcurrentlyStoredVersionOnlySpecInPlace()
    {
        using var store = new TestStore();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Process requests.\n\n## Acceptance criteria\n\n1. Process one request.");
        GoalRefinementWorkCoordinator.RecordPending(kernel, goal.Id);
        await store.Repository.SaveAsync(kernel);
        await store.Repository.EnsureOutboxMessageAsync(GoalRefinementWorkCoordinator.CreateMessage(goal.Id));
        Assert.Null(Assert.IsType<GoalSnapshot>(await store.Repository.LoadGoalAsync(goal.Id)).RefinedSpec);
        Assert.Empty(goal.RefinedSpecVersions);

        var provider = new HeldRefinementProvider();
        ModelFunctionCatalogStore.Save(store.Workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile(provider.ProviderName, "test-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var processing = GoalRefinementWorkCoordinator.ProcessAsync(store.Repository, store.Workspace,
            new InMemoryModelProviderRegistry([provider]), WorkerProfileCatalog.Default(), goal.Id);
        GoalSnapshot expected;
        try
        {
            var entered = await Task.WhenAny(provider.Entered, processing).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Same(provider.Entered, entered);
            kernel.RecordGoalRefinement(goal.Id, CreateSpec("Queue requests."));
            RecordLaterVersions(kernel, goal.Id);
            expected = kernel.ExportGoalSnapshot(goal.Id) with { RefinedSpec = null };
            Assert.All(expected.RefinedSpecVersions!, version => Assert.Equal(1, version.BriefVersion));
            await store.Repository.SaveGoalSnapshotsAsync([expected]);
            await AssertVersionOnlyRowAsync(store.Repository, goal.Id, expected);
        }
        finally
        {
            provider.Release();
            await processing.WaitAsync(TimeSpan.FromSeconds(60));
        }

        var result = await processing;
        Assert.True(result.Claimed);
        Assert.False(result.Attached);
        await AssertVersionOnlyRowAsync(store.Repository, goal.Id, expected);
        Assert.Null(await store.Repository.GetOutboxStateAsync(GoalRefinementWorkCoordinator.MessageId(goal.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreMergeRetainsAdvancedHistoryWithEmptyTopLevelSpec(bool duplicateCurrentSpec)
    {
        using var store = new TestStore();
        var tick = new AgentOrchestratorKernel();
        var goal = tick.CreateGoal("Process requests.");
        tick.RecordGoalRefinement(goal.Id, CreateSpec("Queue requests."));
        await store.Repository.SaveAsync(tick);
        var baseline = tick.ExportGoalSnapshot(goal.Id) with { RefinedSpec = null };

        var concurrent = new AgentOrchestratorKernel();
        concurrent.ReplaceGoalWithSnapshot(baseline);
        RecordLaterVersions(concurrent, goal.Id);
        var advanced = concurrent.ExportGoalSnapshot(goal.Id);
        var storedSpec = advanced.RefinedSpecVersions![2].Spec;
        await store.Repository.SaveGoalSnapshotsAsync([
            advanced with { RefinedSpec = duplicateCurrentSpec ? storedSpec : null }
        ]);
        if (duplicateCurrentSpec)
        {
            var preChangeRow = Assert.IsType<GoalSnapshot>(await store.Repository.LoadGoalAsync(goal.Id));
            Assert.Equal(JsonSerializer.Serialize(storedSpec, StoreOptions),
                JsonSerializer.Serialize(preChangeRow.RefinedSpec, StoreOptions));
            AssertVersionHistory(advanced, preChangeRow);
        }
        else
        {
            await AssertVersionOnlyRowAsync(store.Repository, goal.Id, advanced);
        }

        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");
        var checkpoint = tick.ExportSnapshot();
        var result = Assert.Single(await store.Repository.SaveGoalSnapshotsWithMergeAsync([
            new GoalSnapshotSaveRequest(baseline, Assert.Single(checkpoint.Goals) with { RefinedSpec = null },
                checkpoint.HumanInputRequests)
        ]));

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        await AssertVersionOnlyRowAsync(store.Repository, goal.Id, advanced);
        var merged = (await store.Repository.LoadAsync()).GetGoal(goal.Id);
        Assert.Equal(GoalPriority.Critical, merged.Priority);
        Assert.Equal(3, merged.AuthoritativeRefinedSpecVersion!.Version);
        Assert.Equal(storedSpec.BehavioralContract, merged.RefinedSpec!.BehavioralContract);
    }

    private static RefinedSpec CreateSpec(string contract) =>
        new(contract, ["Process one request."], VerificationClass.TestVerifiable, [], []);

    private static void RecordLaterVersions(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        kernel.RecordGoalRefinement(goalId, CreateSpec("Prioritize queued requests."));
        kernel.RecordGoalRefinement(goalId, CreateSpec("Process urgent requests first."));
    }

    private static async Task AssertVersionOnlyRowAsync(
        SqliteOrchestratorStateRepository repository, GoalId goalId, GoalSnapshot expected)
    {
        var row = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goalId));
        Assert.Null(row.RefinedSpec);
        AssertVersionHistory(expected, row);
    }

    private static void AssertVersionHistory(GoalSnapshot expected, GoalSnapshot actual)
    {
        Assert.Equal(new[] { 1, 2, 3 }, actual.RefinedSpecVersions!.Select(version => version.Version));
        Assert.Equal(JsonSerializer.Serialize(expected.RefinedSpecVersions, StoreOptions),
            JsonSerializer.Serialize(actual.RefinedSpecVersions, StoreOptions));
    }

    private sealed class TestStore : IDisposable
    {
        private readonly string _directory = CreateTempDirectory();

        public TestStore()
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_directory);
            Repository = CreateMigratedStateRepository(Workspace.SqliteStatePath);
        }

        public OrchestratorWorkspace Workspace { get; }
        public SqliteOrchestratorStateRepository Repository { get; }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_directory);
    }

    private sealed class HeldRefinementProvider : IModelProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderName => "single-storage-refiner";
        public Task Entered => _entered.Task;
        public void Release() => _released.TrySetResult();

        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            return new ModelResponse("""
                {"behavioralContract":"Provider contract","acceptanceCriteria":[{"text":"Process one request.","phase":"PreLanding","declared_index":1}],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
                """, new ModelUsage(1, 1), "stop");
        }
    }
}
