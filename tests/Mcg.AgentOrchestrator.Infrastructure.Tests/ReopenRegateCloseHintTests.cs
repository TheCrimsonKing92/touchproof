using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private kernels and unique intent/decision databases; no processes or globals.
public sealed class ReopenRegateCloseHintTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Theory]
    [InlineData(WorkTaskStatus.Assigned)]
    [InlineData(WorkTaskStatus.Failed)]
    [InlineData(WorkTaskStatus.Completed)]
    public async Task Failed_goal_closable_task_refuses_with_numbered_close_command(WorkTaskStatus status)
    {
        using var harness = new Harness();
        var (kernel, goal, task) = FailedGoal(status);
        Assert.Equal(GoalStatus.Failed, goal.Status);
        Assert.Equal(status, task.Status);
        Assert.Equal(2, TaskDisplayNumber.Resolve(goal, task.Id));
        var before = kernel.ExportGoalSnapshot(goal.Id) with { Timeline = [] };
        var intent = await harness.Enqueue(goal, task);

        var result = harness.Coordinator.ExecutePending(kernel, goal);

        Assert.True(result.RejectedAdjudication);
        Assert.False(result.MutatedGoalState);
        await harness.AssertRefusal(intent,
            $"goal-not-acceptance-failed; task is closable: use adjudicate --goal {goal.Id.Value[..8]} 2 close");
        Assert.Equal(JsonSerializer.Serialize(before),
            JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id) with { Timeline = [] }));
    }

    [Fact]
    public async Task Failed_goal_cancelled_task_refuses_with_only_existing_route_hint()
    {
        using var harness = new Harness();
        var (kernel, goal, task) = FailedGoal(WorkTaskStatus.Cancelled);
        Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
        var intent = await harness.Enqueue(goal, task);

        var result = harness.Coordinator.ExecutePending(kernel, goal);

        Assert.True(result.RejectedAdjudication);
        Assert.False(result.MutatedGoalState);
        await harness.AssertRefusal(intent,
            "goal-not-acceptance-failed; task is Cancelled (cancel-dispatch): use route --cause <cause>");
        Assert.Equal(GoalStatus.Failed, goal.Status);
        Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
    }

    [Fact]
    public async Task Acceptance_failed_goal_reopens_and_returns_to_verified()
    {
        using var harness = new Harness();
        var (kernel, goal) = SimpleGoal("Reopen acceptance failure");
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("focused check", harness.Root, 0, "passed", "", DateTimeOffset.UtcNow));
        Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "gate started"));
        Assert.True(kernel.ReconcileGoalAcceptanceFailed(goal.Id, ["gate"], "gate failed"));
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        var intent = await harness.Enqueue(goal, task);

        var result = harness.Coordinator.ExecutePending(kernel, goal);
        harness.Coordinator.CompletePersisted([goal.Id]);

        Assert.True(result.MutatedGoalState);
        Assert.False(result.RejectedAdjudication);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        var decision = await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal(EffectReceiptStatus.Applied, decision!.Effect!.Status);
        Assert.Equal(OperatorIntentStatus.Applied, (await harness.Store.GetAsync(intent.Id))!.Status);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) FailedGoal(WorkTaskStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Refuse reopening a failed goal",
            [new TaskSpec(TaskId.New(), "Earlier task", AgentRole.Researcher),
             new TaskSpec(TaskId.New(), "Operator target", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var snapshot = kernel.ExportSnapshot();
        var saved = snapshot.Goals.Single();
        kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = [saved with
            {
                Status = GoalStatus.Failed,
                Tasks = saved.Tasks.Select((task, index) => task with
                {
                    Status = index == 1 ? status : WorkTaskStatus.Completed
                }).ToArray()
            }]
        });
        goal = kernel.GetGoal(goal.Id);
        return (kernel, goal, goal.Tasks[1]);
    }

    private sealed class Harness : IDisposable
    {
        internal string Root { get; } = CreateTempDirectory("mcg-reopen-close-hint");
        internal SqliteOperatorIntentStore Store { get; }
        internal CollaborationItemStore Decisions { get; }
        internal OperatorIntentCoordinator Coordinator { get; }

        internal Harness()
        {
            Store = new(Path.Combine(Root, "operator-intents.db"), Path.Combine(Root, "logs"));
            Decisions = CollaborationItemStore.ForDirectory(Root);
            Coordinator = new(Store, decisions: Decisions, goalStateVersionResolver: _ => 7);
        }

        internal Task<OperatorIntentRecord> Enqueue(Goal goal, TaskSpec task)
        {
            var id = Guid.NewGuid().ToString("N");
            var payload = new AdjudicateOperatorIntentPayload(
                "reopen-regate", "Operator explanation", ["receipt-1"], 7, Root);
            return Store.EnqueueAsync(new OperatorIntentRecord(
                id, id, OperatorIntentVerbs.Adjudicate, goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [], "operator", "cli", "local-process", DateTimeOffset.UtcNow,
                ActorKind: OperatorActorKind.Human));
        }

        internal async Task AssertRefusal(OperatorIntentRecord intent, string expected)
        {
            var decision = await Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
            Assert.Equal(EffectReceiptStatus.Rejected, decision!.Effect!.Status);
            Assert.Equal(expected, decision.Effect.Result);
            Assert.Equal("refused " + expected, decision.Effect.Outcome);
            Assert.Equal(OperatorIntentStatus.Rejected, (await Store.GetAsync(intent.Id))!.Status);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
