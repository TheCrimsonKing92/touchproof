using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its stores and lease. Failure hooks are scoped to its execution context.
public sealed class KeyedGoalCreationIntentTests : CliCommandTestBase
{
    [Fact]
    public async Task EmptyWorkspace_QueuesThenAppliesUnderTheConductorLease()
    {
        using var root = new TemporaryDirectory();
        var workspace = CreateRefinedWorkspace(root.Path);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        const string key = "empty-workspace";

        var output = Execute(workspace, "goal", "Implement leased workspace intake", "--request-key", key);
        Assert.Contains("\"state\":\"still-committing\"", output);
        Assert.Contains("conductor apply-intents --workspace", output);
        Assert.Empty(await repository.ListGoalMetadataAsync());
        var inbox = OpenInbox(workspace);
        var queued = Assert.Single(await inbox.ListForGoalAsync(OperatorIntentScopes.Workspace));
        Assert.Equal(OperatorIntentVerbs.GoalCreate, queued.Verb);
        Assert.Equal(key, queued.IdempotencyKey);
        Assert.Equal(OperatorIntentStatus.Pending, queued.Status);

        using (ConductorLoopLease.Acquire(workspace.OrchestratorDirectory))
        {
            using var error = new StringWriter();
            var opened = false;
            var code = CliConductorCommand.Run(["conductor", "apply-intents", "--workspace"], workspace,
                lockProbe: new ConductorVerbStartTests.FixedProbe(null), error: error,
                stateRepository: _ => { opened = true; return repository; });
            Assert.Equal(1, code);
            Assert.Contains("conduct-loop.lock", error.ToString());
            Assert.False(opened);
            Assert.Empty(await repository.ListGoalMetadataAsync());
            Assert.Equal(OperatorIntentStatus.Pending, (await inbox.GetAsync(queued.Id))!.Status);
            Assert.Contains("\"state\":\"still-committing\"", Execute(workspace, "goal-intake-status", key));
        }

        Assert.Contains("result=applied", ApplyWorkspace(workspace));
        var created = Assert.Single(await repository.ListGoalMetadataAsync());
        var status = Execute(workspace, "goal-intake-status", key);
        Assert.Contains("\"state\":\"created\"", status);
        Assert.Contains($"\"goalId\":\"{created.Id}\"", status);
        Assert.Equal(OperatorIntentStatus.Applied, (await inbox.GetAsync(queued.Id))!.Status);
        Assert.Contains("No pending workspace operator intents.", ApplyWorkspace(workspace));
    }

    [Fact]
    public async Task ReconstructedCoordinator_AppliesClaimAndRecoversAfterCommitWithoutDuplicatingGraph()
    {
        using var root = new TemporaryDirectory();
        var workspace = CreateRefinedWorkspace(root.Path);
        const string key = "restart";
        Assert.Contains("\"state\":\"still-committing\"", Execute(workspace,
            "goal", "Add a durable restart receipt", "--pipeline", "scout", "--request-key", key));

        // Simulate interruption after claiming but before application starts.
        var claimed = await OpenInbox(workspace).ClaimNextAsync(OperatorIntentScopes.Workspace, OperatorIntentCoordinator.ClaimOwner);
        Assert.NotNull(claimed);
        var reopened = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        Assert.Empty(await reopened.ListGoalMetadataAsync());
        var working = new AgentOrchestratorKernel();
        using (ConductorLoopLease.Acquire(workspace.OrchestratorDirectory))
        using (GoalCreationSideEffectDelivery.UseBeforeEffectDelivery(_ => throw new IOException("interrupted acknowledgement")))
        {
            var coordinator = OperatorIntentCoordinator.CreateDefault(workspace, reopened);
            Assert.Contains("result=deferred", Assert.Single(coordinator.ExecuteWorkspacePending(working)));
        }

        var stored = new GoalIntakeRequestStore(workspace.SqliteStatePath).Get(key)!;
        Assert.Equal(GoalIntakeRequestStates.Created, stored.State);
        Assert.Equal(OperatorIntentStatus.Claimed, (await OpenInbox(workspace).GetAsync(claimed.Id))!.Status);
        var goalId = new GoalId(stored.GoalId!);
        Assert.Equal(goalId, Assert.Single(working.Goals).Id);
        var first = (await reopened.LoadGoalAsync(goalId))!;
        Assert.Equal(4, first.Tasks.Count);
        var graph = JsonSerializer.Serialize(first.Tasks);

        var recoveredKernel = new AgentOrchestratorKernel();
        using (ConductorLoopLease.Acquire(workspace.OrchestratorDirectory))
        {
            var recovered = OperatorIntentCoordinator.CreateDefault(workspace,
                new SqliteOrchestratorStateRepository(workspace.SqliteStatePath));
            Assert.Contains("result=applied-recovered", Assert.Single(recovered.ExecuteWorkspacePending(recoveredKernel)));
            Assert.Empty(recovered.ExecuteWorkspacePending(recoveredKernel));
        }
        Assert.Equal(goalId, Assert.Single(recoveredKernel.Goals).Id);
        Assert.Equal(stored, new GoalIntakeRequestStore(workspace.SqliteStatePath).Get(key));
        Assert.Equal(goalId.Value, Assert.Single(await reopened.ListGoalMetadataAsync()).Id);
        Assert.Equal(graph, JsonSerializer.Serialize((await reopened.LoadGoalAsync(goalId))!.Tasks));
        Assert.Equal(OperatorIntentStatus.Applied, (await OpenInbox(workspace).GetAsync(claimed.Id))!.Status);
    }

    [Fact]
    public async Task ConstructionFailure_RollsBackGoalAndReportsStableFailureThroughStatus()
    {
        using var root = new TemporaryDirectory();
        var workspace = CreateRefinedWorkspace(root.Path);
        const string key = "failed-construction";
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Add durable status rendering");
        string[] args = ["simple-goal", "Add durable status rendering", "--backlog-item", item.Id,
            "--backlog-coverage", "full", "--request-key", key];
        Assert.Contains("\"state\":\"still-committing\"", Execute(workspace, args));
        GoalId? attempted = null;
        using (GoalCreationSideEffectDelivery.UseBeforeStateCommit(goalId =>
        {
            attempted = goalId;
            throw new InvalidOperationException("FIXTURE_CONSTRUCTION_FAILED after the goal graph and source claim were prepared");
        }))
        {
            Assert.Contains("result=rejected", ApplyWorkspace(workspace));
        }

        var requests = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        var receipt = requests.Get(key)!;
        Assert.Equal(GoalIntakeRequestStates.Failed, receipt.State);
        Assert.Equal("FIXTURE_CONSTRUCTION_FAILED", receipt.FailureCode);
        Assert.Contains("after the goal graph and source claim were prepared", receipt.FailureDetail);
        Assert.Null(receipt.GoalId);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        Assert.NotNull(attempted);
        Assert.Null(await repository.LoadGoalAsync(attempted!));
        var kernel = await repository.LoadAsync();
        Assert.Empty(kernel.Goals.SelectMany(goal => goal.Tasks));
        Assert.Empty(kernel.Goals);
        Assert.Null(new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(kernel, item.Id));
        Assert.Empty(await repository.ListOutboxMessagesAsync(GoalCreationSideEffectDelivery.OutboxKind));
        var status = Execute(workspace, "goal-intake-status", key);
        Assert.Contains("\"state\":\"failed\"", status);
        Assert.Contains("\"failureCode\":\"FIXTURE_CONSTRUCTION_FAILED\"", status);
        Assert.Contains(receipt.FailureDetail!, status);
        var failure = Assert.Throws<InvalidOperationException>(() => Execute(workspace, args));
        Assert.StartsWith("GOAL_INTAKE_REPLAY_FAILED ", failure.Message);
        Assert.Contains(receipt.FailureDetail!, failure.Message);
        Assert.Equal(receipt, requests.Get(key));
        Assert.Equal(OperatorIntentStatus.Rejected, Assert.Single(await OpenInbox(workspace).ListForGoalAsync(OperatorIntentScopes.Workspace)).Status);
        Assert.Empty(await repository.ListGoalMetadataAsync());
    }

    [Fact]
    public async Task GoalIntake_concurrent_same_key_creates_one_goal()
    {
        using var root = new TemporaryDirectory();
        var workspace = CreateRefinedWorkspace(root.Path);
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);

        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        Task<string> Start() => Task.Run(() =>
        {
            IReadOnlyList<AgentDefinition> localAgents = AgentCatalog.Default().Agents;
            var localProfiles = WorkerProfileCatalog.Default();
            Goal? localGoal = null;
            ready.Signal();
            release.Wait();
            return CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["simple-goal", "Concurrent keyed task", "--request-key", "concurrent-goal-key"],
                new SqliteOrchestratorStateRepository(workspace.SqliteStatePath),
                workspace,
                ref localAgents,
                new InMemoryModelProviderRegistry([]),
                ref localProfiles,
                ref localGoal));
        });

        var first = Start();
        var second = Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(15)), "Both goal attempts did not reach the event gate.");
        release.Set();
        var receipts = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(receipts, receipt => Assert.Contains("\"state\":\"still-committing\"", receipt));
        Assert.Empty(await repository.ListGoalMetadataAsync());
        var inbox = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        Assert.Single(await inbox.ListForGoalAsync(OperatorIntentScopes.Workspace));
        ApplyWorkspace(workspace);
        var goal = Assert.Single((await repository.LoadAsync()).Goals);
        var task = Assert.Single(goal.Tasks);
        ApplyWorkspace(workspace);
        var replayed = Assert.Single((await repository.LoadAsync()).Goals);
        Assert.Equal(goal.Id, replayed.Id);
        Assert.Equal(task.Id, Assert.Single(replayed.Tasks).Id);
        Assert.Equal(
            GoalIntakeRequestStates.Created,
            new GoalIntakeRequestStore(workspace.SqliteStatePath).Get("concurrent-goal-key")!.State);
    }

    internal static string ApplyWorkspace(OrchestratorWorkspace workspace, int expectedExitCode = 0)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = CliConductorCommand.Run(["conductor", "apply-intents", "--workspace"], workspace,
            output: output, error: error);
        var receipt = output.ToString() + error;
        Assert.True(exit == expectedExitCode, $"Expected exit {expectedExitCode}, got {exit}. {receipt}");
        return receipt;
    }

    private static SqliteOperatorIntentStore OpenInbox(OrchestratorWorkspace workspace) =>
        SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);

    private static string Execute(OrchestratorWorkspace workspace, params string[] args)
    {
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        return CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(args, repository, workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref goal));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("keyed-goal-intent-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
