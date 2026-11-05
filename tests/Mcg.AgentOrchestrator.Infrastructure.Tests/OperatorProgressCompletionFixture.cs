using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its databases, files and kernel; console capture is async-local.
internal sealed class OperatorProgressCompletionFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-progress-{Guid.NewGuid():N}");
    internal AgentOrchestratorKernel Kernel { get; private set; } = new();
    internal OrchestratorWorkspace Workspace { get; }
    internal SqliteOrchestratorStateRepository Repository { get; }
    internal SqliteOperatorIntentStore Store { get; }
    internal GoalId GoalId { get; }
    internal TaskId TaskId { get; }
    internal Goal Goal => Kernel.GetGoal(GoalId);
    internal TaskSpec Task => Goal.FindTask(TaskId);

    internal OperatorProgressCompletionFixture()
    {
        Directory.CreateDirectory(Root);
        Workspace = OrchestratorWorkspace.ForDirectory(Root);
        StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
        Repository = new(Workspace.SqliteStatePath);
        TaskId = TaskId.New();
        var goal = Kernel.CreateGoal("Operator progress", [new TaskSpec(TaskId, "Work", AgentRole.Developer)]);
        GoalId = goal.Id;
        Kernel.ActivateGoal(GoalId, AgentCatalog.Default().Agents);
        Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
    }

    internal Task Save() => Repository.SaveAsync(Kernel);
    internal async Task Reload() => Kernel = await Repository.LoadAsync();
    internal Task<OperatorIntentRecord> Enqueue(ProgressOperatorIntentPayload payload,
        OperatorActorKind kind = OperatorActorKind.Human, string channel = "cli", string assurance = "local-process")
    {
        var id = Guid.NewGuid().ToString("N");
        return Store.EnqueueAsync(new(id, id, OperatorIntentVerbs.Progress, GoalId.Value, TaskId.Value,
            JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), [], "inspector", channel, assurance,
            DateTimeOffset.UtcNow, ActorKind: kind));
    }

    internal ProgressOperatorIntentPayload Completion(string note = "Operator inspected work") =>
        new(WorkTaskStatus.Completed, note, new(true, note, Root));

    internal OperatorIntentExecutionResult Apply(OperatorIntentCoordinator? coordinator = null) =>
        (coordinator ?? new OperatorIntentCoordinator(Store)).ExecutePending(Kernel, Goal);

    internal async Task<OperatorIntentOfflineOutcome> ApplyOffline() =>
        await OperatorIntentGoalApplication.ApplyOffline(Workspace, Repository,
            new OperatorIntentCoordinator(Store), new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(Workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
                TrunkBranchName.Default, Workspace.ExecutionDirectory), GoalId);

    internal string Submit(IReadOnlyList<string> args)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? current = Goal;
        return AsyncLocalConsoleRouter.Capture(() =>
            AsyncLocalConsoleRouter.Error.CaptureLocal(() =>
            {
                var parts = CliArgumentParser.NormalizeArgs(args.ToArray());
                CliCommandHelp.ThrowIfInvalidFlags(parts);
                Assert.False(CliPersistentStateRunner.ExecuteCommand(parts, Repository, Workspace,
                    ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current));
            }));
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
