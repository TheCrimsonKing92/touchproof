using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorIntentAppliedBlockedTests
{
    [Xunit.Fact]
    public async Task RouteOnTesterWhileDeveloperFailedReportsAppliedBlockedNamingDeveloper()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, "incomplete-scope-declaration");
        fixture.Kernel.EscalateTaskFailure(fixture.Goal.Id, fixture.Developer.Id, "Retry budget exhausted.");
        var intent = await fixture.Adjudicate(fixture.Tester, "route");

        await fixture.Apply(intent, fixture.Developer, "incomplete-scope-declaration");

        Assert.Equal(WorkTaskStatus.Assigned, fixture.Tester.Status);
        Assert.Equal(RetryCause.NewTestFinding, fixture.Tester.PendingRetryCause);
        Assert.Equal(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(fixture.Goal));
        Assert.Single(fixture.BlockedRecords(intent));
    }

    [Xunit.Fact]
    public async Task BlockedApplicationIsRecordedOnceAcrossRecoveredClaim()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, "incomplete-scope-declaration");
        var intent = await fixture.Adjudicate(fixture.Tester, "route");
        fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        Assert.Equal(OperatorIntentStatus.Claimed, (await fixture.Store.GetAsync(intent.Id))!.Status);

        // Relaunch after goal persistence, before the intent store's terminal completion.
        var snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot()))!;
        var recoveredKernel = AgentOrchestratorKernel.FromSnapshot(snapshot);
        var recoveredGoal = recoveredKernel.GetGoal(fixture.Goal.Id);
        var recovered = fixture.CreateCoordinator();
        recovered.ExecutePending(recoveredKernel, recoveredGoal);
        recovered.ExecutePending(recoveredKernel, recoveredGoal);

        var stored = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, stored.Status);
        Assert.StartsWith("AppliedBlocked; recovered", stored.Outcome);
        Assert.Contains(fixture.Developer.Id.Value, stored.Outcome, StringComparison.Ordinal);
        Assert.Contains("incomplete-scope-declaration", stored.Outcome, StringComparison.Ordinal);
        Assert.Single(recoveredGoal.Timeline.Where(item =>
            item.OperatorIntentApplied is { BlockedBy: not null } applied && applied.IntentId == intent.Id));
        Assert.Single(recoveredGoal.Timeline.Where(item => item.TaskId == fixture.Tester.Id &&
            item.Kind == ProgressKind.TaskRetried));
    }

    [Xunit.Fact]
    public async Task ProgressCompletedOnDispatchFailedTaskLeavingFailureReportsAppliedBlocked()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, "incomplete-scope-declaration");
        fixture.Fail(fixture.Tester, "dispatch-failed");
        fixture.Kernel.EscalateTaskFailure(fixture.Goal.Id, fixture.Developer.Id, "Retry budget exhausted.");
        const string note = "Operator completed the Tester work.";
        var intent = await fixture.Enqueue(fixture.Tester, OperatorIntentVerbs.Progress,
            new ProgressOperatorIntentPayload(WorkTaskStatus.Completed, note,
                new ManualVerificationRequest(true, note, fixture.Root)));

        await fixture.Apply(intent, fixture.Developer, "incomplete-scope-declaration");

        Assert.Equal(WorkTaskStatus.Completed, fixture.Tester.Status);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Developer.Status);
        Assert.Equal(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(fixture.Goal));
    }

    [Xunit.Fact]
    public async Task VerifyManualLeavingTaskFailedReportsAppliedBlockedOnItself()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, "dispatch-failed");
        var intent = await fixture.Enqueue(fixture.Developer, OperatorIntentVerbs.VerifyManual,
            new ManualVerificationOperatorIntentPayload(Request:
                new ManualVerificationRequest(false, "Manual verification failed.", fixture.Root)));

        await fixture.Apply(intent, fixture.Developer, "dispatch-failed");

        Assert.Equal(WorkTaskStatus.Failed, fixture.Developer.Status);
    }

    [Xunit.Fact]
    public async Task RouteOnGoalsOwnFailedTaskStillReportsApplied()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, "incomplete-scope-declaration");
        fixture.Kernel.EscalateTaskFailure(fixture.Goal.Id, fixture.Developer.Id, "Retry budget exhausted.");
        var intent = await fixture.Adjudicate(fixture.Developer, "route");
        fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        fixture.Coordinator.CompletePersisted([fixture.Goal.Id]);

        var stored = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, stored.Status);
        Assert.StartsWith("Applied adjudicate", stored.Outcome);
        Assert.Empty(fixture.BlockedRecords(intent));
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Developer.Status);
        Assert.NotEqual(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(fixture.Goal));
    }

    [Xunit.Fact]
    public async Task CloseLeavingAnotherFailureReportsAppliedBlocked()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, "incomplete-scope-declaration");
        fixture.Fail(fixture.Tester, "dispatch-failed");
        var intent = await fixture.Adjudicate(fixture.Tester, "close");

        await fixture.Apply(intent, fixture.Developer, "incomplete-scope-declaration");

        Assert.Equal(WorkTaskStatus.Completed, fixture.Tester.Status);
    }

    [Xunit.Fact]
    public async Task ChangedFailureEndsPendingAssociationButPreservesHistoricalOutcome()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, "first-rule");
        var intent = await fixture.Adjudicate(fixture.Tester, "route");
        await fixture.Apply(intent, fixture.Developer, "first-rule");

        fixture.Fail(fixture.Developer, "second-rule");

        Assert.Empty(OperatorIntentEffectCheck.PendingBlocked(fixture.Goal));
        Assert.StartsWith("AppliedBlocked", (await fixture.Store.GetAsync(intent.Id))!.Outcome);
        Assert.Single(fixture.BlockedRecords(intent));
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "intent-blocked-" + Guid.NewGuid().ToString("N"));
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; }
        internal TaskSpec Developer => Goal.Tasks[0];
        internal TaskSpec Tester => Goal.Tasks[1];
        internal SqliteOperatorIntentStore Store { get; }
        internal OperatorIntentCoordinator Coordinator { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Goal = Kernel.CreateGoal("Report task intent effectiveness", [
                new(TaskId.New(), "Implement", AgentRole.Developer),
                new(TaskId.New(), "Test", AgentRole.Tester)]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            Store = new SqliteOperatorIntentStore(Path.Combine(Root, "intents.db"), Path.Combine(Root, "logs"));
            Coordinator = CreateCoordinator();
        }

        internal OperatorIntentCoordinator CreateCoordinator() => new(Store,
            decisions: CollaborationItemStore.ForDirectory(Root), goalStateVersionResolver: _ => 7);

        internal void Fail(TaskSpec task, string rule)
        {
            if (task.Status == WorkTaskStatus.Assigned)
                Kernel.RecordTaskDispatch(Goal.Id, task.Id, new TaskDispatchRecord("test-worker", "test.exe", Root, DateTimeOffset.UtcNow));
            // Deliberately unrelated prose: the outcome must read the typed rule.
            Kernel.ReportTaskProgress(Goal.Id, task.Id, WorkTaskStatus.Failed, "Worker did not complete.",
                failure: new ProgressFailurePayload("TaskFailed", rule, []));
        }

        internal Task<OperatorIntentRecord> Adjudicate(TaskSpec task, string shape) => Enqueue(task,
            OperatorIntentVerbs.Adjudicate, new AdjudicateOperatorIntentPayload(shape,
                "Operator explanation", ["receipt-1"], 7, Root, Cause: nameof(RetryCause.NewTestFinding)));

        internal Task<OperatorIntentRecord> Enqueue<T>(TaskSpec task, string verb, T payload)
        {
            var id = Guid.NewGuid().ToString("N");
            return Store.EnqueueAsync(new OperatorIntentRecord(id, id, verb, Goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), [],
                "operator", "cli", "local-process", DateTimeOffset.UtcNow));
        }

        internal IEnumerable<ProgressEvent> BlockedRecords(OperatorIntentRecord intent) => Goal.Timeline.Where(item =>
            item.OperatorIntentApplied is { BlockedBy: not null } applied && applied.IntentId == intent.Id);

        internal async Task Apply(OperatorIntentRecord intent, TaskSpec blocker, string rule)
        {
            Coordinator.ExecutePending(Kernel, Goal);
            Coordinator.CompletePersisted([Goal.Id]);
            var stored = (await Store.GetAsync(intent.Id))!;
            Assert.Equal(OperatorIntentStatus.Applied, stored.Status);
            Assert.StartsWith("AppliedBlocked", stored.Outcome);
            Assert.Contains(blocker.Id.Value, stored.Outcome, StringComparison.Ordinal);
            Assert.Contains(blocker.RequiredRole.ToString(), stored.Outcome, StringComparison.Ordinal);
            Assert.Contains(rule, stored.Outcome, StringComparison.Ordinal);
            Assert.Contains($"close or route task {Goal.Tasks.ToList().IndexOf(blocker) + 1}", stored.Outcome, StringComparison.Ordinal);
            Assert.Equal(blocker.Id.Value, Assert.Single(BlockedRecords(intent)).OperatorIntentApplied!.BlockedBy!.TaskId);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
