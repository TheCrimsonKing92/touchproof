using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = OperatorIntentAppliedBlockedTests.Fixture;

// Parallel-safe: each fixture owns its database/root, and dispatch and escalation use local callbacks.
public sealed class AppliedBlockedIntentSchedulingTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Fact]
    public async Task ClosingBlockingDeveloperLetsRoutedTesterDispatchNextTick()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, "incomplete-scope-declaration");
        fixture.Kernel.EscalateTaskFailure(fixture.Goal.Id, fixture.Developer.Id, "Retry budget exhausted.");
        var route = await fixture.Adjudicate(fixture.Tester, "route");
        var dispatched = new List<TaskId>();
        var escalations = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                var task = Assert.Single(goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned));
                dispatched.Add(task.Id);
                fixture.Kernel.RecordTaskDispatch(goal.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", fixture.Root, DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, reason) => escalations.Add(reason));

        ApplyIntentTick(fixture, driver);

        Assert.Empty(dispatched);
        Assert.Empty(escalations);
        Assert.StartsWith("AppliedBlocked", (await fixture.Store.GetAsync(route.Id))!.Outcome);

        Tick(fixture, driver);

        Assert.Empty(dispatched);
        Assert.StartsWith("AppliedBlocked", (await fixture.Store.GetAsync(route.Id))!.Outcome);
        Assert.Contains(escalations, reason => reason.Contains(route.Id, StringComparison.Ordinal));
        var close = await fixture.Adjudicate(fixture.Developer, "close");

        ApplyIntentTick(fixture, driver);

        Assert.Empty(dispatched);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Tester.Status);

        Tick(fixture, driver);

        Assert.Equal(fixture.Tester.Id, Assert.Single(dispatched));
        Assert.Equal(WorkTaskStatus.Running, fixture.Tester.Status);
        Assert.Equal(RetryCause.NewTestFinding, fixture.Tester.PendingRetryCause);
        Assert.StartsWith("Applied adjudicate", (await fixture.Store.GetAsync(close.Id))!.Outcome);
        Assert.Equal(2, (await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value)).Count);
        Assert.Empty(OperatorIntentEffectCheck.PendingBlocked(fixture.Goal));
        Assert.StartsWith("AppliedBlocked", (await fixture.Store.GetAsync(route.Id))!.Outcome);
    }

    [Xunit.Fact]
    public async Task RepeatedEscalationNamesPendingBlockedIntentWhileBlockerStands()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, "incomplete-scope-declaration");
        fixture.Kernel.EscalateTaskFailure(fixture.Goal.Id, fixture.Developer.Id, "Retry budget exhausted.");
        var route = await fixture.Adjudicate(fixture.Tester, "route");
        var escalations = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => throw new InvalidOperationException("Failed Developer must block dispatch."),
            writeEscalation: (_, _, reason) => escalations.Add(reason));

        ApplyIntentTick(fixture, driver);
        Assert.Empty(escalations);

        Tick(fixture, driver);
        Tick(fixture, driver);

        Assert.Equal(2, escalations.Count);
        Assert.Equal(escalations[0], escalations[1]);
        Assert.All(escalations, reason =>
        {
            Assert.Contains($"pending AppliedBlocked intent {route.Id}", reason, StringComparison.Ordinal);
            Assert.Contains(fixture.Developer.Id.Value, reason, StringComparison.Ordinal);
            Assert.Contains("incomplete-scope-declaration", reason, StringComparison.Ordinal);
        });
        Assert.Single(fixture.BlockedRecords(route));

        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot());
        var reasonAfterRelaunch = ConductorOutcomeText.BuildTerminalEscalationReason(
            restored.GetGoal(fixture.Goal.Id), GoalLifecycleState.Failed, text => text);
        Assert.Contains(route.Id, reasonAfterRelaunch, StringComparison.Ordinal);

        // Closing the blocker ends the association; the historical outcome remains intact.
        var close = await fixture.Adjudicate(fixture.Developer, "close");
        fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        fixture.Coordinator.CompletePersisted([fixture.Goal.Id]);
        Assert.StartsWith("Applied adjudicate", (await fixture.Store.GetAsync(close.Id))!.Outcome);
        Assert.DoesNotContain(route.Id, ConductorOutcomeText.BuildTerminalEscalationReason(
            fixture.Goal, GoalLifecycle.ResolveState(fixture.Goal), text => text), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task EffectiveRouteOnOwnFailedTaskDispatches()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, "incomplete-scope-declaration");
        fixture.Kernel.EscalateTaskFailure(fixture.Goal.Id, fixture.Developer.Id, "Retry budget exhausted.");
        var route = await fixture.Adjudicate(fixture.Developer, "route");
        var dispatched = new List<TaskId>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                var task = goal.Tasks.First(task => task.Status == WorkTaskStatus.Assigned);
                dispatched.Add(task.Id);
                fixture.Kernel.RecordTaskDispatch(goal.Id, task.Id,
                    new TaskDispatchRecord("test-worker", "test.exe", fixture.Root, DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            });

        ApplyIntentTick(fixture, driver);

        Assert.Empty(dispatched);
        Assert.StartsWith("Applied adjudicate", (await fixture.Store.GetAsync(route.Id))!.Outcome);

        Tick(fixture, driver);

        Assert.Equal(fixture.Developer.Id, Assert.Single(dispatched));
        Assert.Equal(WorkTaskStatus.Running, fixture.Developer.Status);
        Assert.StartsWith("Applied adjudicate", (await fixture.Store.GetAsync(route.Id))!.Outcome);
        Assert.Empty(fixture.BlockedRecords(route));
    }

    private static void ApplyIntentTick(Fixture fixture, ConductorDriver driver)
    {
        // Intent changes are persisted in their own tick before the goal can be scheduled.
        var summary = Tick(fixture, driver);
        Assert.Equal(1, summary.Ticks);
        Assert.Equal(1, summary.Held);
        Assert.Equal(0, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
    }

    private static BatchLoopSummary Tick(Fixture fixture, ConductorDriver driver) =>
        new ConductorBatchLoop(operatorIntents: fixture.Coordinator).Run(
            fixture.Kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(),
            maxIterations: 1, persistGoalTick: (_, _) => { });
}
