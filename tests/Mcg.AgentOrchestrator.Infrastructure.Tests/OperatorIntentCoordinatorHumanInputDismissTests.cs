using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its kernel and stores.
public sealed class OperatorIntentCoordinatorHumanInputDismissTests
{
    [Theory]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Completed)]
    public async Task Apply_OpenDismissibleRequest_CompletesOnlyTarget(GoalStatus status)
    {
        using var fixture = new Fixture();
        var kernel = fixture.Kernel;
        var goal = kernel.CreateGoal("Apply a dismissal", [new TaskSpec(TaskId.New(), "Linked task", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var taskId = goal.Tasks.Single().Id;
        var answered = kernel.RecordAnsweredOperatorHumanInput(goal.Id, "Earlier decision", "Preserve this answer");
        var target = kernel.RequestHumanInput(goal.Id, taskId, "Dismiss this", isDismissible: true);
        var other = kernel.RequestHumanInput(goal.Id, null, "Keep this", isDismissible: true);
        var item = await fixture.Collaboration.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value, "Keep this", "Open item");
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [snapshot.Goals.Single() with { Status = status }] });
        goal = kernel.GetGoal(goal.Id);
        var before = kernel.ExportSnapshot();
        var intent = await fixture.Enqueue(goal, target.Id);

        Assert.True(fixture.Coordinator.ExecutePending(kernel, goal).MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Claimed, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        fixture.Coordinator.CompletePersisted([goal.Id]);

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        Assert.True(kernel.HumanInputRequests.Single(request => request.Id == target.Id).WasDismissed);
        Assert.False(kernel.HumanInputRequests.Single(request => request.Id == other.Id).IsCompleted);
        Assert.Equal("Preserve this answer", kernel.HumanInputRequests.Single(request => request.Id == answered.Id).Answer);
        Assert.Equal(item.Status, Assert.Single(await fixture.Collaboration.ListAsync()).Status);
        var after = kernel.ExportSnapshot();
        var priorGoal = before.Goals.Single();
        var savedGoal = after.Goals.Single();
        if (status == GoalStatus.Active)
        {
            Assert.Equal(WorkTaskStatus.Running, goal.Tasks.Single().Status);
        }
        else
        {
            Assert.Equal(status, savedGoal.Status);
            Assert.Equal(JsonSerializer.Serialize(priorGoal.Tasks), JsonSerializer.Serialize(savedGoal.Tasks));
            Assert.Equal(JsonSerializer.Serialize(priorGoal.Timeline),
                JsonSerializer.Serialize(savedGoal.Timeline.Take(priorGoal.Timeline.Count)));
            Assert.Collection(savedGoal.Timeline.Skip(priorGoal.Timeline.Count),
                entry => Assert.StartsWith("Dismissed human wait", entry.Message),
                entry => Assert.Equal(intent.Id, entry.OperatorIntentApplied?.IntentId));
        }
    }

    [Theory]
    [InlineData("unknown", "was not found")]
    [InlineData("completed", "already completed")]
    [InlineData("required", "is not dismissible")]
    [InlineData("different-goal", "does not belong")]
    public async Task Apply_IneligibleRequest_RejectsWithoutStateChanges(string target, string reason)
    {
        using var fixture = new Fixture();
        var kernel = fixture.Kernel;
        var goal = kernel.CreateGoal("Reject an ineligible dismissal");
        var other = kernel.CreateGoal("Other goal");
        var answered = kernel.RecordAnsweredOperatorHumanInput(goal.Id, "Earlier decision", "Preserve this answer");
        var required = kernel.RequestHumanInput(goal.Id, null, "Keep this", isDismissible: false);
        var dismissible = kernel.RequestHumanInput(other.Id, null, "Owned by another goal", isDismissible: true);
        await fixture.Collaboration.RaiseAsync(CollaborationItemType.Verify, goal.Id.Value, "Keep this", "Open item");
        var requestId = target switch
        {
            "completed" => answered.Id,
            "required" => required.Id,
            "different-goal" => dismissible.Id,
            _ => HumanInputRequestId.New()
        };
        var before = JsonSerializer.Serialize(kernel.ExportSnapshot());
        var beforeItems = JsonSerializer.Serialize(await fixture.Collaboration.ListAsync());
        var intent = await fixture.Enqueue(goal, requestId);

        Assert.False(fixture.Coordinator.ExecutePending(kernel, goal).MutatedGoalState);

        var rejected = Assert.IsType<OperatorIntentRecord>(await fixture.Intents.GetAsync(intent.Id));
        Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
        Assert.Contains(requestId.Value, rejected.Outcome);
        Assert.Contains(reason, rejected.Outcome);
        Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportSnapshot()));
        Assert.Equal(beforeItems, JsonSerializer.Serialize(await fixture.Collaboration.ListAsync()));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"dismiss-intent-{Guid.NewGuid():N}");

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            var workspace = OrchestratorWorkspace.ForDirectory(_root);
            Intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            Collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            Coordinator = new OperatorIntentCoordinator(Intents, decisions: Collaboration,
                goalStateVersionResolver: _ => 0);
        }

        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal SqliteOperatorIntentStore Intents { get; }
        internal CollaborationItemStore Collaboration { get; }
        internal OperatorIntentCoordinator Coordinator { get; }

        internal Task<OperatorIntentRecord> Enqueue(Goal goal, HumanInputRequestId requestId)
        {
            var id = Guid.NewGuid().ToString("N");
            return Intents.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.HumanInputDismiss, goal.Id.Value, null,
                JsonSerializer.Serialize(new HumanInputDismissOperatorIntentPayload(requestId.Value), OperatorIntentJson.Options),
                [], "operator", "test", "test", DateTimeOffset.UtcNow));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
