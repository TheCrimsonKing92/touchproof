using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each case owns its stores and uses the shared synchronized console capture.
public sealed class CliAttentionDismissHumanInputTests : CliCommandTestBase
{
    private protected override bool UsesGit => false;

    [Fact]
    public async Task DismissItem_ExactRequest_QueuesOneIntentAndPrintsReceipt()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Dismiss one request");
        var target = kernel.RequestHumanInput(goal.Id, null, "Dismiss this", isDismissible: true);
        var other = kernel.RequestHumanInput(goal.Id, null, "Keep this", isDismissible: true);
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var item = await collaboration.RaiseAsync(CollaborationItemType.Verify, goal.Id.Value, "Keep item", "Open item");
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);

        var (output, changed, _) = ExecuteCliAndCaptureResult(["attention", "dismiss", "--item", target.Id.Value], kernel, workspace);

        Assert.False(changed);
        Assert.False(target.IsCompleted);
        Assert.False(other.IsCompleted);
        Assert.Equal(item.Status, Assert.Single(await collaboration.ListAsync()).Status);
        var intent = Assert.Single(await intents.ListForGoalAsync(goal.Id.Value));
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Equal(OperatorIntentVerbs.HumanInputDismiss, intent.Verb);
        Assert.Equal(goal.Id.Value, intent.GoalId);
        Assert.Null(intent.TaskId);
        Assert.Equal(target.Id.Value, ReadRequestId(intent));
        Assert.Contains($"Operator intent queued: id={intent.Id}", output);
        Assert.Contains($"request={target.Id.Value} status=Pending", output);
    }

    [Theory]
    [InlineData("unknown", "No attention item or human-input request")]
    [InlineData("prefix", "No attention item matches id")]
    [InlineData("completed", "already completed")]
    [InlineData("required", "is not dismissible")]
    public async Task DismissItem_IneligibleRequest_LeavesStoresUnchanged(string kind, string reason)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Validate single dismissal");
        var dismissible = kernel.RequestHumanInput(goal.Id, null, "Open dismissible", isDismissible: true);
        var required = kernel.RequestHumanInput(goal.Id, null, "Open non-dismissible", isDismissible: false);
        var completed = kernel.RequestHumanInput(goal.Id, null, "Earlier answer");
        kernel.SubmitHumanInput(completed.Id, "Keep the answer");
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var item = await collaboration.RaiseAsync(CollaborationItemType.Verify, goal.Id.Value, "Keep item", "Open item");
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var id = kind switch
        {
            "prefix" => dismissible.Id.Value[..8],
            "completed" => completed.Id.Value,
            "required" => required.Id.Value,
            _ => HumanInputRequestId.New().Value
        };
        var before = JsonSerializer.Serialize(kernel.ExportSnapshot());

        var error = Record.Exception(() => ExecuteCliAndCapture(["attention", "dismiss", "--item", id], kernel, workspace));

        Assert.NotNull(error);
        Assert.Contains(id, error.Message);
        Assert.Contains(reason, error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportSnapshot()));
        Assert.Empty(await intents.ListForGoalAsync(goal.Id.Value));
        Assert.Equal(item.Status, Assert.Single(await collaboration.ListAsync()).Status);
    }

    [Fact]
    public async Task DismissGoal_MixedAttention_ReportsCountsAndOnlyQueuesDismissibleRequests()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Dismiss mixed attention");
        var first = kernel.RequestHumanInput(goal.Id, null, "First dismissible", isDismissible: true);
        var second = kernel.RequestHumanInput(goal.Id, null, "Second dismissible", isDismissible: true);
        var required = kernel.RequestHumanInput(goal.Id, null, "Keep required", isDismissible: false);
        var answered = kernel.RequestHumanInput(goal.Id, null, "Earlier answer");
        kernel.SubmitHumanInput(answered.Id, "Preserved answer");
        var other = kernel.CreateGoal("Other goal");
        var otherWait = kernel.RequestHumanInput(other.Id, null, "Do not dismiss", isDismissible: true);
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var verify = await collaboration.RaiseAsync(CollaborationItemType.Verify, goal.Id.Value, "Dismiss verification", "Verify");
        var decision = await collaboration.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value, "Dismiss decision", "Decide");
        var unrelated = await collaboration.RaiseAsync(CollaborationItemType.Verify, other.Id.Value, "Keep verification", "Verify");
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);

        var (output, changed, _) = ExecuteCliAndCaptureResult(["attention", "dismiss", "--goal", goal.Id.Value[..8]], kernel, workspace);

        Assert.False(changed);
        Assert.Contains("collaboration-dismissed=2 human-input-dismiss-queued=2 non-dismissible-skipped=1", output);
        var items = (await collaboration.ListAsync()).ToDictionary(item => item.Id);
        Assert.Equal(CollaborationItemStatus.Resolved, items[verify.Id].Status);
        Assert.Equal(CollaborationItemStatus.Resolved, items[decision.Id].Status);
        Assert.Equal(unrelated.Status, items[unrelated.Id].Status);
        var queued = await intents.ListForGoalAsync(goal.Id.Value);
        Assert.Equal(new[] { first.Id.Value, second.Id.Value }.Order(), queued.Select(ReadRequestId).Order());
        Assert.Empty(await intents.ListForGoalAsync(other.Id.Value));
        Assert.All(queued, intent =>
        {
            Assert.Equal(OperatorIntentVerbs.HumanInputDismiss, intent.Verb);
            Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        });
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        var coordinator = new OperatorIntentCoordinator(intents);
        foreach (var _ in queued)
        {
            Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);
        }

        Assert.All(await intents.ListForGoalAsync(goal.Id.Value), intent => Assert.Equal(OperatorIntentStatus.Applied, intent.Status));
        Assert.True(first.IsCompleted);
        Assert.True(second.IsCompleted);
        Assert.False(required.IsCompleted);
        Assert.False(otherWait.IsCompleted);
        Assert.Equal("Preserved answer", answered.Answer);
    }

    [Theory]
    [InlineData("attention dismiss --item")]
    [InlineData("attention dismiss [--goal]")]
    public void AttentionUsage_EachDismissForm_MentionsHumanInputRequests(string form)
    {
        var segment = Assert.Single(CliCommandHelp.AttentionUsage.Split(" | ")
            .Where(part => part.StartsWith(form, StringComparison.Ordinal)));
        Assert.Contains("human-input request", segment);
    }

    private static string ReadRequestId(OperatorIntentRecord intent) =>
        JsonSerializer.Deserialize<HumanInputDismissOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!.RequestId;
}
