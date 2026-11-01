using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its kernel, database and driver; all execution seams are local.
public sealed class AwaitingVerificationOperatorWaitTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task Blocked_gate_ticks_show_one_wait_then_clear_after_verification()
    {
        using var fixture = new Fixture();
        var task = fixture.Goal.Tasks.Single();
        fixture.RecordGate(task, passed: false);
        Assert.Equal(GoalLifecycleState.AwaitingVerification, GoalLifecycle.ResolveState(fixture.Goal));
        Assert.Equal(VerificationGateStatus.FailedVerification,
            Assert.Single(AgentOrchestratorKernel.BuildVerificationWorklist(fixture.Goal).Items).GateStatus);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            fixture.Driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(GoalLifecycleState.AwaitingVerification, held.State);

        fixture.Tick();

        var first = Assert.Single(await fixture.Waits());
        Assert.Equal(CollaborationItemType.Decision, first.Type);
        AssertGateText(first.Body, fixture.Goal, task, 1, "FailedVerification");
        Assert.Equal(GoalLifecycleState.AwaitingVerification, GoalLifecycle.ResolveState(fixture.Goal));
        Assert.Null(fixture.Goal.CurrentHold);
        await fixture.AssertSurfaces(first);

        fixture.Tick();

        var second = Assert.Single(await fixture.Waits());
        Assert.Equal(first.Id, second.Id);
        await fixture.AssertSurfaces(second);

        fixture.RecordGate(task, passed: true);
        Assert.Empty(AgentOrchestratorKernel.BuildVerificationWorklist(fixture.Goal).Items);
        fixture.Tick();

        Assert.Empty(await fixture.Waits());
        Assert.Empty((await fixture.ReadDecisions()).Entries);
        Assert.DoesNotContain(first.Id, await fixture.AttentionItemIds());
    }

    [Fact]
    public async Task Empty_worklist_at_awaiting_verification_never_raises_wait()
    {
        using var fixture = new Fixture();
        fixture.RecordGate(fixture.Goal.Tasks.Single(), passed: true);
        fixture.SetGoalStatus(GoalStatus.Active);
        Assert.Equal(GoalLifecycleState.AwaitingVerification, GoalLifecycle.ResolveState(fixture.Goal));
        Assert.Empty(AgentOrchestratorKernel.BuildVerificationWorklist(fixture.Goal).Items);

        fixture.Driver.AwaitingVerificationGateAttention!.Reconcile(fixture.Goal, held: true);

        Assert.Empty(await fixture.Waits());
        Assert.Empty((await fixture.ReadDecisions()).Entries);
        Assert.Empty(await fixture.AttentionItemIds());
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task Changed_worklist_refreshes_same_wait_and_lists_every_blocked_task()
    {
        using var fixture = new Fixture(taskCount: 4);
        fixture.Tick();
        var first = Assert.Single(await fixture.Waits());
        Assert.Equal(4, AgentOrchestratorKernel.BuildVerificationWorklist(fixture.Goal).Items.Count);
        for (var index = 0; index < fixture.Goal.Tasks.Count; index++)
            AssertGateText(first.Body, fixture.Goal, fixture.Goal.Tasks[index], index + 1, "MissingVerification");

        fixture.RecordGate(fixture.Goal.Tasks[0], passed: true);
        fixture.RecordGate(fixture.Goal.Tasks[1], passed: false);
        fixture.Tick();

        var refreshed = Assert.Single(await fixture.Waits());
        Assert.Equal(first.Id, refreshed.Id);
        Assert.NotEqual(first.Body, refreshed.Body);
        Assert.DoesNotContain($"verify-manual {fixture.Goal.Id.Value[..8]} 1 passed", refreshed.Body);
        AssertGateText(refreshed.Body, fixture.Goal, fixture.Goal.Tasks[1], 2, "FailedVerification");
        AssertGateText(refreshed.Body, fixture.Goal, fixture.Goal.Tasks[2], 3, "MissingVerification");
        AssertGateText(refreshed.Body, fixture.Goal, fixture.Goal.Tasks[3], 4, "MissingVerification");
        await fixture.AssertSurfaces(refreshed);
    }

    [Theory]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Cancelled)]
    public async Task State_exit_clears_wait_preserves_other_items_and_allows_reentry(GoalStatus status)
    {
        using var fixture = new Fixture();
        var unrelated = await fixture.Store.RaiseAsync(CollaborationItemType.Decision,
            fixture.Goal.Id.Value, "Other operator work", "Keep this item", "other:" + fixture.Goal.Id.Value);
        var attention = fixture.Driver.AwaitingVerificationGateAttention!;
        attention.Reconcile(fixture.Goal, held: true);
        var first = Assert.Single(await fixture.Waits());

        fixture.SetGoalStatus(status);
        Assert.NotEqual(GoalLifecycleState.AwaitingVerification, GoalLifecycle.ResolveState(fixture.Goal));
        attention.ReconcileAll([fixture.Goal]);

        Assert.Empty(await fixture.Waits());
        Assert.Equal(unrelated.Id, Assert.Single(await fixture.Store.GetAttentionQueueAsync()).Id);
        Assert.Empty((await fixture.ReadDecisions()).Entries);

        fixture.SetGoalStatus(GoalStatus.Active);
        Assert.Equal(GoalLifecycleState.AwaitingVerification, GoalLifecycle.ResolveState(fixture.Goal));
        attention.Reconcile(fixture.Goal, held: true);

        var reentered = Assert.Single(await fixture.Waits());
        Assert.NotEqual(first.Id, reentered.Id);
        Assert.Equal(first.CorrelationKey, reentered.CorrelationKey);
        await fixture.AssertSurfaces(reentered);
        Assert.Contains(await fixture.Store.GetAttentionQueueAsync(), item => item.Id == unrelated.Id);
    }

    private static void AssertGateText(string body, Goal goal, TaskSpec task, int number, string gate) =>
        Assert.Contains($"task {number} role={task.RequiredRole} gate={gate}: " +
            $"verify-manual {goal.Id.Value[..8]} {number} passed", body, StringComparison.Ordinal);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = CreateTempDirectory("mcg-awaiting-gate-wait");
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; private set; }
        internal CollaborationItemStore Store { get; }
        internal ConductorDriver Driver { get; }

        internal Fixture(int taskCount = 1)
        {
            var roles = new[] { AgentRole.Developer, AgentRole.Tester, AgentRole.Researcher, AgentRole.Reviewer };
            Goal = Kernel.CreateGoal("Operator task gates", Enumerable.Range(0, taskCount)
                .Select(index => new TaskSpec(TaskId.New(), $"Task {index + 1}", roles[index])).ToArray());
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            foreach (var task in Goal.Tasks)
                Kernel.ReportTaskProgress(Goal.Id, task.Id, WorkTaskStatus.Completed, "done");
            Store = CollaborationItemStore.ForDirectory(_root);
            Driver = MakeDriver(executionDirectory: _root,
                dispatchAndStart: _ => throw new InvalidOperationException("Unexpected worker dispatch"),
                runAcceptance: _ => throw new InvalidOperationException("Unexpected acceptance execution"));
            Driver.AwaitingVerificationGateAttention = new(Store);
        }

        internal void Tick()
        {
            Driver.BeginTick(Kernel, 0);
            new ConductorBatchLoop(processCpuTime: () => TimeSpan.Zero, countLiveGroupedGates: () => 0)
                .Run(Kernel, Driver, ConductorAutonomyPolicy.Conservative,
                    Path.Combine(_root, "stop"), maxIterations: 1, onlyGoalId: Goal.Id.Value);
        }

        internal void RecordGate(TaskSpec task, bool passed) =>
            Kernel.RecordTaskVerification(Goal.Id, task.Id,
                new TaskVerificationRecord("operator gate", _root, passed ? 0 : 1,
                    passed ? "passed" : "", passed ? "" : "failed", DateTimeOffset.UtcNow));

        internal void SetGoalStatus(GoalStatus status)
        {
            var snapshot = Kernel.ExportSnapshot();
            Kernel.ReplaceWithSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal with { Status = status }).ToArray()
            });
            Goal = Kernel.GetGoal(Goal.Id);
        }

        internal async Task<IReadOnlyList<CollaborationItem>> Waits() =>
            (await Store.GetAttentionQueueAsync()).Where(item =>
                item.CorrelationKey == AwaitingVerificationGateAttention.Prefix + Goal.Id.Value).ToArray();

        internal Task<OwnerDecisionRead> ReadDecisions() =>
            OwnerDecisionReader.ReadAsync(new OwnerQuestionReadModel(new StateQueries(Kernel), _root));

        internal async Task<string[]> AttentionItemIds()
        {
            using var json = await AttentionJson();
            return json.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!).ToArray();
        }

        private async Task<JsonDocument> AttentionJson() => JsonDocument.Parse(AttentionShowAgentReadView.Render(
            Goal.Id.Value, [], await Store.GetAttentionQueueAsync(), _ => null,
            goals: [Goal], decisions: await ReadDecisions()));

        internal async Task AssertSurfaces(CollaborationItem wait)
        {
            var decisions = await ReadDecisions();
            var entry = Assert.Single(decisions.Entries);
            Assert.Equal(wait.Id, entry.ItemId);
            Assert.Equal(Goal.Id.Value, entry.GoalId);
            Assert.Equal(wait.Body, entry.Text);
            Assert.False(Assert.Single(decisions.Snapshot.Live).CanAnswer);
            using var json = await AttentionJson();
            var payload = json.RootElement.GetProperty("payload");
            var row = Assert.Single(payload.GetProperty("items").EnumerateArray().Where(item =>
                item.GetProperty("correlationKey").GetString() == wait.CorrelationKey));
            Assert.Equal(wait.Id, row.GetProperty("id").GetString());
            Assert.Equal(wait.Body, row.GetProperty("body").GetString());
            Assert.Equal(wait.Id, Assert.Single(payload.GetProperty("decisions").EnumerateArray())
                .GetProperty("itemId").GetString());
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class StateQueries(AgentOrchestratorKernel kernel) : IOrchestratorStateQueries
    {
        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> ids,
            CancellationToken cancellationToken = default) => Task.FromResult(kernel);

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalSummary>>(kernel.Goals.Select(goal => new GoalSummary(
                goal.Id.Value, goal.Status.ToString(), goal.Objective, "fixture")).ToArray());

        public Task<IReadOnlyList<HumanInputRequestSnapshot>> ListOpenHumanInputRequestsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HumanInputRequestSnapshot>>([]);

        public Task<IReadOnlyList<TerminalOwnerQuestionHold>> ListTerminalOwnerQuestionHoldsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TerminalOwnerQuestionHold>>([]);
    }
}
