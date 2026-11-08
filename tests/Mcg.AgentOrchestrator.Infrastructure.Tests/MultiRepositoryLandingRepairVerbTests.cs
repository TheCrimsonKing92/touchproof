using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned Git, SQLite and receipt state; AsyncLocal console capture.
public sealed class MultiRepositoryLandingRepairVerbTests
{
    [Fact]
    public void Registration_RootAndCommandHelp_ExposeTheHoldNextAction()
    {
        var verb = MultiRepositoryLandingFailedHold.NextAction;
        Assert.Equal(verb, OperatorIntentVerbs.MultiRepositoryLandingRepair);
        Assert.Contains(verb, CliArgumentParser.RecognizedCommands);
        Assert.True(CliHandledVerbRegistry.IsHandled(verb));
        Assert.True(CliMultiRepositoryLandingRepairCommand.IsCommand([verb.ToUpperInvariant()]));
        var commandHelp = InfrastructureTestSupport.CaptureConsole(() =>
            Assert.True(CliCommandHelp.TryPrintStartupHelp(["help", verb])));
        var rootHelp = InfrastructureTestSupport.CaptureConsole(() =>
            Assert.True(CliCommandHelp.TryPrintStartupHelp(["--help"])));
        Assert.Contains("Usage: " + verb + " <goal-prefix>", commandHelp);
        Assert.Contains(verb, rootHelp);
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags([verb, "--unknown"]));
    }

    [Fact]
    public async Task Run_ResolvedPrefix_QueuesOneIntentWithoutChangingGoalOrLanding()
    {
        using var f = new GoalRepositoriesTestFixture();
        var goal = f.CreateGoal();
        await InfrastructureTestSupport.CreateMigratedStateRepository(f.Workspace.SqliteStatePath).SaveAsync(f.Kernel);
        var records = new MultiRepositoryLandingRecordStore(f.Workspace.OrchestratorDirectory);
        records.Write(new MultiRepositoryLandingFailure(goal.Id, "fixture tuple", "default", "fixture",
            new Dictionary<string, IReadOnlyList<string>>(), [], DateTimeOffset.UtcNow));
        records.Write(new MultiRepositoryLandingAttempt(goal.Id, "fixture tuple", []));
        var before = JsonSerializer.Serialize(new { Failure = records.ReadFailure(goal.Id), Attempt = records.ReadAttempt(goal.Id) });
        var version = await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(f.Workspace.SqliteStatePath, goal.Id.Value);
        Assert.NotNull(version);
        var status = goal.Status;
        var exit = -1;

        InfrastructureTestSupport.CaptureConsole(() => exit = CliMultiRepositoryLandingRepairCommand.Run(
            [OperatorIntentVerbs.MultiRepositoryLandingRepair, goal.Id.Value[..8], "--idempotency-key", "repair-case"], f.Workspace));

        Assert.Equal(0, exit);
        var store = SqliteOperatorIntentStore.ForDirectories(f.Workspace.OrchestratorDirectory, f.Workspace.LogDirectory);
        var intent = Assert.Single(await store.ListForGoalAsync(goal.Id.Value));
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Equal(OperatorIntentVerbs.MultiRepositoryLandingRepair, intent.Verb);
        Assert.Equal(goal.Id.Value, intent.GoalId);
        Assert.Equal("repair-case", intent.IdempotencyKey);
        Assert.Equal(version, await SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(f.Workspace.SqliteStatePath, goal.Id.Value));
        Assert.Equal(before, JsonSerializer.Serialize(new { Failure = records.ReadFailure(goal.Id), Attempt = records.ReadAttempt(goal.Id) }));
        Assert.Equal(status, goal.Status);

        // Apply through the real tick dispatcher, using empty compensation to isolate intent wiring.
        var coordinator = new OperatorIntentCoordinator(store, orchestratorDirectory: f.Workspace.OrchestratorDirectory)
        {
            LandingRepair = new(f.Workspace, f.Registry)
        };
        var applied = coordinator.ExecutePending(f.Kernel, goal);
        Assert.True(applied.MutatedGoalState, string.Join(Environment.NewLine, applied.ProgressLines));
        Assert.Null(records.ReadFailure(goal.Id));
        Assert.Null(records.ReadAttempt(goal.Id));
        coordinator.CompletePersisted([goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
    }

    [Theory]
    [InlineData(false, "unknown-goal")]
    [InlineData(true, "ambiguous-goal-prefix")]
    public async Task Run_UnresolvedPrefix_RejectsWithoutEnqueuing(bool ambiguous, string reason)
    {
        using var f = new GoalRepositoriesTestFixture();
        f.CreateGoal();
        f.CreateGoal();
        var snapshot = f.Kernel.ExportSnapshot();
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select((goal, index) => goal with
                { Id = "12345678" + index.ToString("D24") }).ToArray()
        });
        await InfrastructureTestSupport.CreateMigratedStateRepository(f.Workspace.SqliteStatePath).SaveAsync(kernel);
        var prefix = ambiguous ? "12345678" : "not-a-goal";
        var exit = -1;
        var error = InfrastructureTestSupport.CaptureConsoleError(() =>
            InfrastructureTestSupport.CaptureConsole(() => exit = CliMultiRepositoryLandingRepairCommand.Run(
                [OperatorIntentVerbs.MultiRepositoryLandingRepair, prefix], f.Workspace)));
        Assert.Equal(1, exit);
        Assert.Contains(reason, error);
        var store = SqliteOperatorIntentStore.ForDirectories(f.Workspace.OrchestratorDirectory, f.Workspace.LogDirectory);
        foreach (var item in kernel.Goals) Assert.Empty(await store.ListForGoalAsync(item.Id.Value));
    }

    [Fact]
    public async Task Tick_NoFailure_RejectsWithNamedReason()
    {
        using var f = new GoalRepositoriesTestFixture();
        var goal = f.CreateGoal();
        var store = SqliteOperatorIntentStore.ForDirectories(f.Workspace.OrchestratorDirectory, f.Workspace.LogDirectory);
        var id = Guid.NewGuid().ToString("N");
        await store.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.MultiRepositoryLandingRepair,
            goal.Id.Value, null, "{}", [], "operator", "cli", "local", DateTimeOffset.UtcNow));
        var coordinator = new OperatorIntentCoordinator(store, orchestratorDirectory: f.Workspace.OrchestratorDirectory)
        {
            LandingRepair = new(f.Workspace, f.Registry)
        };
        var result = coordinator.ExecutePending(f.Kernel, goal);
        Assert.Contains(result.ProgressLines, line => line.Contains("no-failure-record", StringComparison.Ordinal));
        Assert.Equal(OperatorIntentStatus.Rejected, (await store.GetAsync(id))!.Status);
    }

}
