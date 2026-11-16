using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every test owns its workspace and SQLite stores, with an injected clock.
public sealed class TaskIntentAdmissionExpiredAttentionTests
{
    [Fact]
    public async Task ExpiredTaskIntent_WithStore_RejectsUnchangedAndRaisesOneOpenDecision()
    {
        using var f = new Fixture();
        var intent = await f.Store.EnqueueAsync(f.ManualVerification());
        f.Now = intent.AdmissionExpiresAt!.Value;
        var before = JsonSerializer.Serialize(f.Kernel.ExportGoalSnapshot(f.Goal.Id));
        var coordinator = f.Coordinator();

        var result = coordinator.ExecutePending(f.Kernel, f.Goal);

        Assert.False(result.MutatedGoalState);
        Assert.Equal(before, JsonSerializer.Serialize(f.Kernel.ExportGoalSnapshot(f.Goal.Id)));
        await AssertExpiredRejection(f, intent, result);
        var item = Assert.Single(await f.Attention.GetAttentionQueueAsync());
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Equal(CollaborationItemStatus.Raised, item.Status);
        Assert.Equal(f.Goal.Id.Value, item.GoalId);
        Assert.Equal("task-intent-admission-expired:" + intent.Id, item.CorrelationKey);
        Assert.Contains(intent.Verb, item.Subject);
        Assert.Contains("task-intent-admission-expired", item.Subject);
        Assert.Contains(intent.Id, item.Body);
        Assert.Contains("Task 1 (" + f.Goal.Tasks.Single().Id.Value + ")", item.Body);

        Assert.Empty(coordinator.ExecutePending(f.Kernel, f.Goal).ProgressLines);
        Assert.Equal(item.Id, Assert.Single(await f.Attention.GetAttentionQueueAsync()).Id);
    }

    [Fact]
    public async Task RepeatRaiseForSameIntent_LeavesOneOpenItem()
    {
        using var f = new Fixture();
        var intent = await f.Store.EnqueueAsync(f.ManualVerification());
        f.Now = intent.AdmissionExpiresAt!.Value;
        f.Coordinator().ExecutePending(f.Kernel, f.Goal);
        var original = Assert.Single(await f.Attention.GetAttentionQueueAsync());
        var rejected = (await f.Store.GetAsync(intent.Id))!;

        var first = TaskIntentAdmissionExpiredAttention.Raise(f.Attention, f.Goal, rejected);
        var second = TaskIntentAdmissionExpiredAttention.Raise(f.Attention, f.Goal, rejected);

        Assert.Equal(original.Id, first.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Id, Assert.Single(await f.Attention.ListAsync(f.Goal.Id.Value)).Id);
        var open = Assert.Single(await f.Attention.GetAttentionQueueAsync());
        Assert.Equal("task-intent-admission-expired:" + intent.Id, open.CorrelationKey);
        Assert.False(CollaborationItemLifecycle.IsTerminal(open.Status));
    }

    [Fact]
    public async Task DifferentExpiredIntents_RaiseSeparateItems()
    {
        using var f = new Fixture();
        var first = await f.Store.EnqueueAsync(f.ManualVerification());
        var second = await f.Store.EnqueueAsync(f.ManualVerification());
        f.Now = second.AdmissionExpiresAt!.Value;

        f.Coordinator().ExecutePending(f.Kernel, f.Goal);

        var items = await f.Attention.GetAttentionQueueAsync();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.CorrelationKey == "task-intent-admission-expired:" + first.Id);
        Assert.Contains(items, item => item.CorrelationKey == "task-intent-admission-expired:" + second.Id);
    }

    [Fact]
    public async Task ExpiredTaskIntent_WithoutStore_RejectsExactlyAsBefore()
    {
        using var f = new Fixture();
        var intent = await f.Store.EnqueueAsync(f.ManualVerification());
        f.Now = intent.AdmissionExpiresAt!.Value.AddSeconds(1);

        var result = f.Coordinator(withStore: false).ExecutePending(f.Kernel, f.Goal);

        Assert.False(result.MutatedGoalState);
        await AssertExpiredRejection(f, intent, result);
        Assert.Empty(await f.Attention.ListAsync(f.Goal.Id.Value));
    }

    [Fact]
    public async Task UnexpiredIntent_RaisesNoExpiryItem()
    {
        using var f = new Fixture();
        var intent = await f.Store.EnqueueAsync(f.ManualVerification());
        f.Now = intent.AdmissionExpiresAt!.Value.AddTicks(-1);
        var coordinator = f.Coordinator();

        var result = coordinator.ExecutePending(f.Kernel, f.Goal);
        Assert.True(result.MutatedGoalState);
        coordinator.CompletePersisted([f.Goal.Id]);

        Assert.Equal(OperatorIntentStatus.Applied, (await f.Store.GetAsync(intent.Id))!.Status);
        Assert.Single(f.Goal.Timeline, item => item.OperatorIntentApplied?.IntentId == intent.Id);
        Assert.Empty(await f.Attention.ListAsync(f.Goal.Id.Value));
    }

    [Fact]
    public async Task ExpiredIntentRecoveredAsApplied_RaisesNoExpiryItem()
    {
        using var f = new Fixture();
        var intent = await f.Store.EnqueueAsync(f.ManualVerification());
        Assert.True(f.Coordinator().ExecutePending(f.Kernel, f.Goal).MutatedGoalState);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(f.Workspace.SqliteStatePath);
        await repository.SaveAsync(f.Kernel); // Simulate a crash before completing the claimed intent.
        Assert.Equal(OperatorIntentStatus.Claimed, (await f.Store.GetAsync(intent.Id))!.Status);
        var restored = await repository.LoadAsync();
        var goal = restored.GetGoal(f.Goal.Id);
        f.Now = intent.AdmissionExpiresAt!.Value.AddSeconds(1);

        var result = f.Coordinator().ExecutePending(restored, goal);

        Assert.False(result.MutatedGoalState);
        Assert.Equal($"OPERATOR_INTENT id={intent.Id} verb={intent.Verb} goal={goal.Id.Value[..8]} result=applied-recovered",
            Assert.Single(result.ProgressLines));
        Assert.Equal(OperatorIntentStatus.Applied, (await f.Store.GetAsync(intent.Id))!.Status);
        Assert.Single(goal.Timeline, item => item.OperatorIntentApplied?.IntentId == intent.Id);
        Assert.Empty(await f.Attention.ListAsync(goal.Id.Value));
    }

    [Fact]
    public async Task ExpiredTaskGraphRepairWithoutTaskTarget_StatesNoTaskTarget()
    {
        using var f = new Fixture();
        var intent = await f.Store.EnqueueAsync(f.ManualVerification() with
        {
            Verb = OperatorIntentVerbs.TaskGraphRepair,
            TaskId = null,
            PayloadJson = JsonSerializer.Serialize(GraphRepairTestFixture.FiveRoles, JsonSerializerOptions.Web)
        });
        f.Now = intent.AdmissionExpiresAt!.Value;

        var result = f.Coordinator().ExecutePending(f.Kernel, f.Goal);

        await AssertExpiredRejection(f, intent, result);
        var item = Assert.Single(await f.Attention.GetAttentionQueueAsync());
        Assert.Contains(intent.Id, item.Body);
        Assert.Contains(intent.Verb, item.Subject);
        Assert.Contains("No task target is present.", item.Body);
    }

    [Fact]
    public async Task ExpiredIntentWithMissingTask_IdentifiesTargetWithoutThrowing()
    {
        using var f = new Fixture();
        var missingTaskId = TaskId.New().Value;
        var intent = await f.Store.EnqueueAsync(f.ManualVerification() with { TaskId = missingTaskId });
        f.Now = intent.AdmissionExpiresAt!.Value;

        var result = f.Coordinator().ExecutePending(f.Kernel, f.Goal);

        await AssertExpiredRejection(f, intent, result);
        var item = Assert.Single(await f.Attention.GetAttentionQueueAsync());
        Assert.Contains(missingTaskId, item.Body);
        Assert.Contains("not in the current task list", item.Body);
    }

    [Fact]
    public async Task DefaultFactoryCoordinator_RaisesExpiryItem()
    {
        using var f = new Fixture();
        var intent = await f.Store.EnqueueAsync(f.ManualVerification());
        var coordinator = OperatorIntentCoordinator.CreateDefault(f.Workspace);

        var result = coordinator.ExecutePending(f.Kernel, f.Goal);

        await AssertExpiredRejection(f, intent, result);
        var item = Assert.Single(await f.Attention.GetAttentionQueueAsync());
        Assert.Equal("task-intent-admission-expired:" + intent.Id, item.CorrelationKey);
    }

    private static async Task AssertExpiredRejection(Fixture f, OperatorIntentRecord intent,
        OperatorIntentExecutionResult result)
    {
        var completed = await f.Store.GetAsync(intent.Id);
        Assert.NotNull(completed);
        Assert.Equal(OperatorIntentStatus.Rejected, completed.Status);
        Assert.Equal("Rejected: task-intent-admission-expired.", completed.Outcome);
        Assert.Equal($"OPERATOR_INTENT id={intent.Id} verb={intent.Verb} goal={f.Goal.Id.Value[..8]} result=rejected reason=task-intent-admission-expired",
            Assert.Single(result.ProgressLines));
    }

    private sealed class Fixture : IDisposable, IClock
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "mcg-intent-expiry-" + Guid.NewGuid().ToString("N"));
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20000);
        DateTimeOffset IClock.UtcNow => Now;
        internal readonly AgentOrchestratorKernel Kernel;
        internal readonly Goal Goal;
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly SqliteOperatorIntentStore Store;
        internal readonly CollaborationItemStore Attention;

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Kernel = new AgentOrchestratorKernel(this);
            Goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, AgentCatalog.Default().Agents, "Expired intent attention");
            Kernel.ReportTaskProgress(Goal.Id, Goal.Tasks.Single().Id, WorkTaskStatus.Completed, "Work completed.");
            Store = new SqliteOperatorIntentStore(Path.Combine(Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName),
                Workspace.LogDirectory, utcNow: () => Now);
            Attention = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
        }

        internal OperatorIntentCoordinator Coordinator(bool withStore = true) => new(Store, utcNow: () => Now,
            decisions: withStore ? Attention : null, goalStateVersionResolver: withStore ? _ => 1L : null);

        internal OperatorIntentRecord ManualVerification() => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            OperatorIntentVerbs.VerifyManual, Goal.Id.Value, Goal.Tasks.Single().Id.Value,
            JsonSerializer.Serialize(new ManualVerificationOperatorIntentPayload(new TaskVerificationRecord(
                "manual-verification passed", Root, 0, "Operator inspected the result.", "", Now,
                ModelFitNote: "OpenAI/gpt-test - adequate")), JsonSerializerOptions.Web),
            [], "operator", "cli", "local-process", Now);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
