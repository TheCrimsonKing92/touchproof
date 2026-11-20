using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class GoalDependencyTickRaceTests : CliCommandTestBase
{
    private protected override bool UsesGit => false;

    [Theory]
    [InlineData("--clear")]
    [InlineData("--remove")]
    [InlineData("--on")]
    public async Task StaleTick_KeepsOperatorEdgeSetAndPersistsTickPriority(string operation)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Dependent");
        var first = kernel.CreateGoal("First prerequisite");
        var second = kernel.CreateGoal("Second prerequisite");
        kernel.SetGoalDependency(goal.Id, first.Id);
        if (operation == "--remove")
            kernel.SetGoalDependency(goal.Id, second.Id);
        await repository.SaveAsync(kernel);

        var tick = await repository.LoadAsync();
        var baseline = Snapshot(tick, goal.Id);
        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");
        var args = operation switch
        {
            "--clear" => new[] { "goal-depends", goal.Id.Value, operation },
            "--remove" => new[] { "goal-depends", goal.Id.Value, operation, first.Id.Value },
            _ => new[] { "goal-depends", goal.Id.Value, operation, second.Id.Value }
        };
        string[] expected = operation switch
        {
            "--clear" => [],
            "--remove" => [second.Id.Value],
            _ => [first.Id.Value, second.Id.Value]
        };
        Assert.False(SortedEdges(expected).SequenceEqual(SortedEdges(baseline.DependsOn)));
        RunCommand(repository, workspace, args);
        Assert.Equal(SortedEdges(expected), SortedEdges((await repository.LoadGoalAsync(goal.Id))!.DependsOn));

        var result = await SaveTick(repository, baseline, tick, goal.Id);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var persisted = (await repository.LoadAsync()).GetGoal(goal.Id);
        Assert.Equal(SortedEdges(expected), SortedEdges(persisted.DependsOn.Select(id => id.Value)));
        Assert.Equal(GoalPriority.Critical, persisted.Priority);
    }

    [Fact]
    public async Task DependencyConflict_NamesRetainedFieldsInMergeAndReceipt()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Dependent");
        var oldDependency = kernel.CreateGoal("Old prerequisite");
        var newDependency = kernel.CreateGoal("New prerequisite");
        kernel.SetGoalDependency(goal.Id, oldDependency.Id);
        kernel.SetGoalPriority(goal.Id, GoalPriority.High, "test", "initial priority");
        await repository.SaveAsync(kernel);

        var tick = await repository.LoadAsync();
        var baseline = Snapshot(tick, goal.Id);
        tick.ClearGoalDependencies(goal.Id);
        kernel.SetGoalPriority(goal.Id, GoalPriority.Normal, "test", "operator reset");
        await repository.SaveAsync(kernel);
        RunCommand(repository, workspace, ["goal-depends", goal.Id.Value, "--clear"]);
        RunCommand(repository, workspace, ["goal-depends", goal.Id.Value, "--on", newDependency.Id.Value]);

        var result = await SaveTick(repository, baseline, tick, goal.Id);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        var stored = Assert.IsType<GoalSnapshot>(await repository.LoadGoalAsync(goal.Id));
        Assert.Equal(newDependency.Id.Value, Assert.Single(stored.DependsOn!));
        Assert.Null(stored.Priority);
        const string label = "stored value kept over differing tick value: ";
        Assert.Contains(label, result.Message);
        var fields = result.Message!.Split(label)[1].Split(',');
        Assert.Contains(nameof(GoalSnapshot.DependsOn), fields);
        Assert.Contains(nameof(GoalSnapshot.Priority), fields);
        Assert.Equal(fields.Order(StringComparer.Ordinal).Distinct(), fields);
        var receipt = CliPersistentStateRunner.FormatTickMergeReceipt(result);
        Assert.StartsWith($"TICK_MERGE goal={goal.Id.Value[..8]} disposition=MERGED ", receipt);
        Assert.Contains(nameof(GoalSnapshot.DependsOn), receipt);
    }

    [Fact]
    public async Task MergeWithoutRetainedFields_KeepsBaseMessage()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Goal");
        await repository.SaveAsync(kernel);

        var tick = await repository.LoadAsync();
        var baseline = Snapshot(tick, goal.Id);
        tick.SetGoalPriority(goal.Id, GoalPriority.Critical, "test", "tick");
        kernel.SetGoalPriority(goal.Id, GoalPriority.High, "test", "operator");
        await repository.SaveAsync(kernel);

        var result = await SaveTick(repository, baseline, tick, goal.Id);

        Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
        Assert.Equal("stored version advanced during tick; reapplied tick snapshot delta onto fresh goal row", result.Message);
    }

    [Fact]
    public async Task DependencyCommands_ReportCommittedEdgeSet()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Dependent");
        var first = kernel.CreateGoal("First prerequisite");
        var second = kernel.CreateGoal("Second prerequisite");
        kernel.SetGoalDependency(goal.Id, first.Id);
        await repository.SaveAsync(kernel);

        var goalPrefix = goal.Id.Value[..8];
        var firstPrefix = first.Id.Value[..8];
        var secondPrefix = second.Id.Value[..8];
        var set = RunCommand(repository, workspace, ["goal-depends", goal.Id.Value, "--on", second.Id.Value]);
        var both = string.Join(", ", SortedEdges([firstPrefix, secondPrefix]));
        Assert.Contains($"Dependency set: {goalPrefix} depends on {secondPrefix}; stored dependencies: {both}{Environment.NewLine}", set);

        var remove = RunCommand(repository, workspace, ["goal-depends", goal.Id.Value, "--remove", first.Id.Value]);
        Assert.Contains($"Dependency removed: {goalPrefix} no longer depends on {firstPrefix}; stored dependencies: {secondPrefix}{Environment.NewLine}", remove);

        var clear = RunCommand(repository, workspace, ["goal-depends", goal.Id.Value, "--clear"]);
        Assert.Contains($"Dependencies cleared: {goalPrefix}; stored dependencies: none{Environment.NewLine}", clear);
    }

    [Fact]
    public void ReadBackMismatch_ReportsBothSetsAndSuppressesSuccess()
    {
        var (tracked, target, intended, other) = RecordDependencyWrite();

        var output = CaptureConsole(() =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => tracked.Confirm(true, id =>
            {
                Assert.Equal(target, id);
                return [intended.Value, other.Value];
            }));
            Assert.Contains("differ from the intended set", error.Message);
            Assert.Contains($"Intended: {intended.Value}.", error.Message);
            Assert.Contains($"Stored: {string.Join(", ", SortedEdges([intended.Value, other.Value]))}.", error.Message);
        });

        Assert.Empty(output);
    }

    [Fact]
    public void ReadBackFailure_ReportsPersistedDependenciesCouldNotBeVerified()
    {
        var (tracked, target, _, _) = RecordDependencyWrite();
        var failure = new IOException("Test store read failed.");

        var output = CaptureConsole(() =>
        {
            var error = Assert.Throws<InvalidOperationException>(() => tracked.Confirm(true, _ => throw failure));
            Assert.Contains($"Could not verify persisted dependencies for goal {target.Value}", error.Message);
            Assert.Contains("The write may already be committed", error.Message);
            Assert.Same(failure, error.InnerException);
        });

        Assert.Empty(output);
    }

    private (CliGoalDependencyCommands.TrackedArguments Arguments, GoalId Target, GoalId Intended, GoalId Other)
        RecordDependencyWrite()
    {
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal("Dependent");
        var intended = kernel.CreateGoal("Requested prerequisite");
        var other = kernel.CreateGoal("Unexpected prerequisite");
        var args = CliGoalDependencyCommands.Track(
            ["goal-depends", target.Id.Value, "--on", intended.Id.Value], out var tracked);
        var context = new CliExecutionContext(kernel, CreateRefinedWorkspace(CreateTempDirectory()),
            new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), null);

        var output = CaptureConsole(() => Assert.True(CliGoalDependencyCommands.ExecuteGoalDepends(context, args)));
        Assert.Empty(output);
        return (Assert.IsType<CliGoalDependencyCommands.TrackedArguments>(tracked), target.Id, intended.Id, other.Id);
    }

    private static string RunCommand(
        SqliteOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace,
        IReadOnlyList<string> args)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Assert.True(CliPersistentStateRunner.ExecuteCommand(
            args, repository, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal)));
    }

    private static GoalSnapshot Snapshot(AgentOrchestratorKernel kernel, GoalId id) =>
        kernel.ExportSnapshot().Goals.Single(goal => goal.Id == id.Value);

    private static async Task<GoalSnapshotSaveResult> SaveTick(
        SqliteOrchestratorStateRepository repository,
        GoalSnapshot baseline,
        AgentOrchestratorKernel tick,
        GoalId id)
    {
        var snapshot = tick.ExportSnapshot();
        return Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, snapshot.Goals.Single(goal => goal.Id == id.Value), snapshot.HumanInputRequests)]));
    }

    private static string[] SortedEdges(IEnumerable<string>? edges) =>
        (edges ?? []).Order(StringComparer.Ordinal).ToArray();
}
