using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

[Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliAttentionDismissHumanInputPersistenceTestsCliProcessEnvironment : CliCommandTestBase
{
    [Theory]
    [InlineData(GoalStatus.Cancelled, false)]
    [InlineData(GoalStatus.Cancelled, true)]
    [InlineData(GoalStatus.Completed, true)]
    public async Task TerminalDismissal_RoundTripsThroughSqliteAndConductor(GoalStatus status, bool goalScoped)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Terminal goal with open attention",
            [new TaskSpec(TaskId.New(), "Linked task", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var earlier = kernel.RecordAnsweredOperatorHumanInput(goal.Id, "Earlier answer", "Retain answer");
        var taskId = goal.Tasks.Single().Id;
        var target = kernel.RequestHumanInput(goal.Id, taskId, "Dismiss task wait", isDismissible: true);
        var second = kernel.RequestHumanInput(goal.Id, null, "Dismiss goal wait only on goal scope", isDismissible: true);
        var required = kernel.RequestHumanInput(goal.Id, null, "Retain substantive wait", isDismissible: false);
        kernel.ReportTaskProgress(goal.Id, taskId,
            status == GoalStatus.Cancelled ? WorkTaskStatus.Cancelled : WorkTaskStatus.Completed,
            "Task ended before operator dismissal");
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(WithGoalStatus(kernel, goal.Id, status));
        var before = (await repository.LoadAsync()).ExportSnapshot();
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var unrelatedItem = await collaboration.RaiseAsync(CollaborationItemType.Verify, null, "Keep item", "Open item");

        var (changed, output) = RunCommand(goalScoped
            ? ["attention", "dismiss", "--goal", goal.Id.Value[..8]]
            : ["attention", "dismiss", "--item", target.Id.Value], repository, workspace);

        Assert.False(changed);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize((await repository.LoadAsync()).ExportSnapshot()));
        var dismissals = await intents.ListForGoalAsync(goal.Id.Value);
        Assert.Equal(goalScoped ? 2 : 1, dismissals.Count);
        Assert.All(dismissals, intent =>
        {
            Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
            Assert.Equal(OperatorIntentVerbs.HumanInputDismiss, intent.Verb);
            Assert.Contains($"Operator intent queued: id={intent.Id}", output);
        });
        Assert.Contains($"request={target.Id.Value} status=Pending", output);
        if (goalScoped)
            Assert.Contains("collaboration-dismissed=0 human-input-dismiss-queued=2 non-dismissible-skipped=1", output);

        var progressId = Guid.NewGuid().ToString("N");
        if (status == GoalStatus.Cancelled)
            await intents.EnqueueAsync(new OperatorIntentRecord(progressId, progressId, OperatorIntentVerbs.Progress,
                goal.Id.Value, taskId.Value, JsonSerializer.Serialize(
                    new ProgressOperatorIntentPayload(WorkTaskStatus.Running, "This intent must be rejected"), OperatorIntentJson.Options),
                [], "operator", "test", "test", DateTimeOffset.UtcNow));

        foreach (var _ in dismissals)
            RunCommand(["conduct", "--loop", "--max-iterations", "1"], repository, workspace);

        foreach (var dismissal in dismissals)
            Assert.Equal(OperatorIntentStatus.Applied, (await intents.GetAsync(dismissal.Id))!.Status);
        if (status == GoalStatus.Cancelled)
        {
            var rejected = (await intents.GetAsync(progressId))!;
            Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
            Assert.Contains("reasonCode=goal-terminal-evicted", rejected.Outcome);
        }
        var reloaded = await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync();
        Assert.True(reloaded.HumanInputRequests.Single(wait => wait.Id == target.Id).WasDismissed);
        Assert.Equal(goalScoped, reloaded.HumanInputRequests.Single(wait => wait.Id == second.Id).WasDismissed);
        Assert.False(reloaded.HumanInputRequests.Single(wait => wait.Id == required.Id).IsCompleted);
        Assert.Equal("Retain answer", reloaded.HumanInputRequests.Single(wait => wait.Id == earlier.Id).Answer);
        Assert.Equal(unrelatedItem.Status, Assert.Single(await collaboration.ListAsync()).Status);
        var priorGoal = before.Goals.Single();
        var savedGoal = reloaded.ExportSnapshot().Goals.Single();
        Assert.Equal(status, savedGoal.Status);
        Assert.Equal(JsonSerializer.Serialize(priorGoal.Tasks), JsonSerializer.Serialize(savedGoal.Tasks));
        Assert.Equal(JsonSerializer.Serialize(priorGoal.Timeline),
            JsonSerializer.Serialize(savedGoal.Timeline.Take(priorGoal.Timeline.Count)));
        var appended = savedGoal.Timeline.Skip(priorGoal.Timeline.Count).ToArray();
        Assert.Equal(dismissals.Count, appended.Count(entry => entry.Message.StartsWith("Dismissed human wait", StringComparison.Ordinal)));
        Assert.Equal(dismissals.Select(intent => intent.Id).Order(),
            appended.Where(entry => entry.OperatorIntentApplied is not null).Select(entry => entry.OperatorIntentApplied!.IntentId).Order());
        Assert.Equal(dismissals.Count * 2, appended.Length);
    }

    private static (bool Changed, string Output) RunCommand(
        string[] args, SqliteOrchestratorStateRepository repository, OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var changed = false;
        var output = CaptureConsole(() => changed = CliPersistentStateRunner.ExecuteCommand(args, repository, workspace,
            ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));
        return (changed, output);
    }
}
