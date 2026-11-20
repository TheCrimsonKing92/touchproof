using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using DeliveryState = Mcg.AgentOrchestrator.Core.RetryFeedbackDeliveryClassifier.State;

// Parallel-safe: each test owns its kernel and any SQLite files.
public sealed class RetryFeedbackDeliveryTests
{
    private const string Feedback = "First correction.\r\nSecond line.\rFinal line.";
    private const string FeedbackHash = "08fefcdc8a04c8030bc864da6607d8edb56d2fcd1d3e1b4cf3e9dc22f5cdae35";
    private const string SupersededFeedbackHash = "bf8073287e2d1987db267a9edacda52efa951ffde1c8c56b6a7fb12481b73394";
    private static readonly string PromptHash = new('a', 64);
    private static readonly DateTimeOffset AcceptedAt = new(2026, 11, 19, 12, 0, 0, TimeSpan.FromHours(2));

    [Xunit.Fact]
    public void Classify_NoAcceptedFeedback_IgnoresDispatchReceipt()
    {
        var task = NewTask("Task without accepted feedback");
        task.RecordDispatch(Dispatch(AcceptedAt, Receipt(task)));

        Assert.Equal(new RetryFeedbackDeliveryClassifier.Result(DeliveryState.NoAcceptedFeedback),
            RetryFeedbackDeliveryClassifier.Classify(task));
    }

    [Xunit.Fact]
    public void Classify_MatchingDispatchBeforeAcceptance_IsAwaiting()
    {
        var task = TaskWithFeedback();
        var dispatchedAt = AcceptedAt.AddMinutes(-1);
        task.RecordDispatch(Dispatch(dispatchedAt, Receipt(task)));

        AssertAwaiting(task);
    }

    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(1)]
    public void Classify_MatchingDispatchAtOrAfterAcceptance_ReturnsPromptAndTime(int minutesAfterAcceptance)
    {
        var task = TaskWithFeedback();
        var dispatchedAt = AcceptedAt.AddMinutes(minutesAfterAcceptance);
        task.RecordDispatch(Dispatch(dispatchedAt, Receipt(task)));

        Assert.Equal(new RetryFeedbackDeliveryClassifier.Result(
            DeliveryState.Delivered, AcceptedAt, PromptHash, dispatchedAt),
            RetryFeedbackDeliveryClassifier.Classify(task));
    }

    [Xunit.Fact]
    public void Classify_LatestDispatchHasSupersededHash_IsAwaiting()
    {
        var task = TaskWithFeedback();
        task.RecordDispatch(Dispatch(AcceptedAt, Receipt(task)));
        task.RecordDispatch(Dispatch(AcceptedAt.AddMinutes(1), Receipt(task, SupersededFeedbackHash)));

        AssertAwaiting(task);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Classify_MissingPromptReceipt_IsAwaiting(bool hasContextPackageReceipt)
    {
        var task = TaskWithFeedback();
        var contextReceipt = hasContextPackageReceipt ? ContextReceipt(null) : null;
        task.RecordDispatch(new("fixture", "unused", "unused", AcceptedAt.AddMinutes(1),
            ContextPackageReceipt: contextReceipt));

        AssertAwaiting(task);
    }

    [Xunit.Fact]
    public async Task AttentionShow_AppliedRetry_ReportsAwaitingThenDelivered()
    {
        var clock = new FixedClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = NewTask("Retry implementation");
        var noFeedback = NewTask("Task without accepted feedback");
        var goal = kernel.CreateGoal("Retry feedback delivery", [task, noFeedback]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Needs correction.");

        using (var beforeRetry = Render(goal.Id.Value, [goal]))
        {
            Assert.False(beforeRetry.RootElement.GetProperty("payload").TryGetProperty("retryFeedbackDelivery", out _));
        }

        var root = Directory.CreateTempSubdirectory("retry-feedback-delivery-");
        try
        {
            var store = new SqliteOperatorIntentStore(Path.Combine(root.FullName, "operator-intents.db"), root.FullName);
            var intent = new OperatorIntentRecord(
                Guid.NewGuid().ToString("N"), "delivery-test-retry", OperatorIntentVerbs.Retry,
                goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(
                    new RetryOperatorIntentPayload(Feedback, null, RetryCause: RetryCause.ContractClarification),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [], "operator", "cli", "local-process", clock.UtcNow);
            await store.EnqueueAsync(intent);
            var coordinator = new OperatorIntentCoordinator(store, utcNow: () => clock.UtcNow);
            Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);

            Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))?.Status);
            Assert.Null(task.LastDispatch);
            using var awaiting = Render(goal.Id.Value, [goal]);
            AssertDeliveryRow(awaiting, goal, task, "awaiting-delivery", null, null);

            var dispatchedAt = AcceptedAt.AddMinutes(1);
            kernel.RecordTaskDispatch(goal.Id, task.Id, Dispatch(dispatchedAt, Receipt(task)));

            using var delivered = Render(goal.Id.Value, [goal]);
            AssertDeliveryRow(delivered, goal, task, "delivered", PromptHash, "2026-11-19T10:01:00.0000000Z");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Xunit.Fact]
    public void AttentionShow_SelectedGoals_UseStoredTaskOrderAndOmitTerminalTasks()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var first = TaskWithFeedback();
        var completed = TaskWithFeedback();
        completed.SetStatus(WorkTaskStatus.Completed);
        var failed = TaskWithFeedback();
        failed.SetStatus(WorkTaskStatus.Failed);
        var cancelled = TaskWithFeedback();
        cancelled.SetStatus(WorkTaskStatus.Cancelled);
        var other = TaskWithFeedback();
        var goal = kernel.CreateGoal("Mixed task statuses", [first, completed, failed, cancelled]);
        var otherGoal = kernel.CreateGoal("Other goal", [other]);

        using var selected = Render(goal.Id.Value, [otherGoal, goal]);
        Assert.Equal([first.Id.Value, failed.Id.Value], ReadDeliveryTaskIds(selected));

        using var all = Render(null, [otherGoal, goal]);
        Assert.Equal([other.Id.Value, first.Id.Value, failed.Id.Value], ReadDeliveryTaskIds(all));
    }

    private static TaskSpec NewTask(string description) => new(TaskId.New(), description, AgentRole.Developer);

    private static TaskSpec TaskWithFeedback()
    {
        var task = NewTask("Task with accepted feedback");
        task.RecordAcceptedRetryFeedback(Feedback, AcceptedAt);
        return task;
    }

    private static TaskDispatchRecord Dispatch(DateTimeOffset dispatchedAt, WorkerRetryFeedbackPromptReceipt receipt) =>
        new("fixture", "unused", "unused", dispatchedAt, ContextPackageReceipt: ContextReceipt(receipt));

    private static WorkerContextPackageReceipt ContextReceipt(WorkerRetryFeedbackPromptReceipt? receipt)
    {
        var unknown = ProviderUsageValue.Unknown("fixture");
        return new("fixture", [], unknown, unknown, unknown, RetryFeedbackPromptReceipt: receipt);
    }

    private static WorkerRetryFeedbackPromptReceipt Receipt(TaskSpec task, string feedbackHash = FeedbackHash) =>
        new("task/criterion-retry-feedback.json", task.Id.Value, AcceptedAt.AddMinutes(-1), feedbackHash,
            "typed-hash", "projection-hash", PromptHash, ContextDeliveryMode.InlineFull);

    private static void AssertAwaiting(TaskSpec task) =>
        Assert.Equal(new RetryFeedbackDeliveryClassifier.Result(DeliveryState.AwaitingDelivery, AcceptedAt),
            RetryFeedbackDeliveryClassifier.Classify(task));

    private static JsonDocument Render(string? goalId, IReadOnlyCollection<Goal> goals) =>
        JsonDocument.Parse(AttentionShowAgentReadView.Render(goalId, [], [], _ => null, goals: goals));

    private static string?[] ReadDeliveryTaskIds(JsonDocument document) =>
        document.RootElement.GetProperty("payload").GetProperty("retryFeedbackDelivery")
            .EnumerateArray().Select(row => row.GetProperty("taskId").GetString()).ToArray();

    private static void AssertDeliveryRow(
        JsonDocument document, Goal goal, TaskSpec task, string state, string? promptHash, string? dispatchedAt)
    {
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var row = Assert.Single(document.RootElement.GetProperty("payload").GetProperty("retryFeedbackDelivery").EnumerateArray());
        Assert.Equal(["goalId", "taskId", "state", "acceptedAt", "promptHash", "dispatchedAt"],
            row.EnumerateObject().Select(property => property.Name));
        Assert.Equal(goal.Id.Value, row.GetProperty("goalId").GetString());
        Assert.Equal(task.Id.Value, row.GetProperty("taskId").GetString());
        Assert.Equal(state, row.GetProperty("state").GetString());
        Assert.Equal("2026-11-19T10:00:00.0000000Z", row.GetProperty("acceptedAt").GetString());
        Assert.Equal(promptHash, row.GetProperty("promptHash").GetString());
        Assert.Equal(dispatchedAt, row.GetProperty("dispatchedAt").GetString());
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => AcceptedAt;
    }
}
