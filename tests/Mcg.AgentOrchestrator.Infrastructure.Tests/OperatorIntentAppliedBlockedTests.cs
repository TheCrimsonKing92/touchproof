using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorIntentAppliedBlockedTests
{
    private const string DispatchRule = DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing;
    private const string VerdictRule = "succeeded-worker-result-failing-tests";
    private const string VerdictGuidance = "close or route task 1 to proceed";
    private const string DispatchGuidance = VerdictGuidance + ", or run recover for this goal";

    [Xunit.Fact]
    public async Task KnownCauseRetryLeavingDispatchFailureReportsAppliedBlocked()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, DispatchRule);
        fixture.Fail(fixture.Tester, VerdictRule);
        var intent = await fixture.Enqueue(fixture.Tester, OperatorIntentVerbs.Retry,
            new RetryOperatorIntentPayload("Retry the Tester work.", null, RetryCause: RetryCause.NewSourceFinding));

        await AssertBlockedApplication(fixture, intent, "AppliedBlocked retry", DispatchRule, DispatchGuidance);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Tester.Status);
    }

    [Xunit.Fact]
    public async Task ClarifiedRetryLeavingDispatchFailureReportsContinuationWithBlocker()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, DispatchRule);
        fixture.Kernel.ReportTaskProgress(fixture.Goal.Id, fixture.Tester.Id,
            WorkTaskStatus.Failed, "Tester work failed.");
        var intent = await fixture.Enqueue(fixture.Tester, OperatorIntentVerbs.Retry,
            new RetryOperatorIntentPayload("Retry the Tester work.", null));

        fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        var clarification = Assert.Single(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
        Assert.Equal(fixture.Tester.Id, clarification.TaskId);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, fixture.Tester.Status);
        Assert.Equal(OperatorIntentStatus.Claimed, (await fixture.Store.GetAsync(intent.Id))!.Status);
        fixture.Kernel.SubmitHumanInput(clarification.Id, nameof(RetryCause.NewSourceFinding));

        await AssertBlockedApplication(fixture, intent, "Applied retry continuation", DispatchRule, DispatchGuidance);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Tester.Status);
        Assert.Equal(RetryCause.NewSourceFinding, fixture.Tester.PendingRetryCause);
    }

    [Xunit.Fact]
    public async Task RetryClearingOnlyVerdictFailureReportsPlainApplied()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, VerdictRule);
        var intent = await fixture.Enqueue(fixture.Developer, OperatorIntentVerbs.Retry,
            new RetryOperatorIntentPayload("Retry the Developer work.", null, RetryCause: RetryCause.NewSourceFinding));

        fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        fixture.Coordinator.CompletePersisted([fixture.Goal.Id]);

        var stored = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, stored.Status);
        Assert.Equal($"Applied retry to goal {fixture.Goal.Id.Value[..8]} task {fixture.Developer.Id.Value[..8]}.",
            stored.Outcome);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Developer.Status);
        Assert.NotEqual(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(fixture.Goal));
        Assert.Empty(fixture.BlockedRecords(intent));
    }

    [Xunit.Fact]
    public async Task RetryLeavingVerdictFailureKeepsCloseOrRouteGuidance()
    {
        using var fixture = new Fixture();
        fixture.Fail(fixture.Developer, VerdictRule);
        fixture.Fail(fixture.Tester, VerdictRule);
        var intent = await fixture.Enqueue(fixture.Tester, OperatorIntentVerbs.Retry,
            new RetryOperatorIntentPayload("Retry the Tester work.", null, RetryCause: RetryCause.NewSourceFinding));

        await AssertBlockedApplication(fixture, intent, "AppliedBlocked retry", VerdictRule, VerdictGuidance);
    }

    private static async Task AssertBlockedApplication(
        Fixture fixture, OperatorIntentRecord intent, string outcomePrefix, string rule, string guidance)
    {
        fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal);
        fixture.Coordinator.CompletePersisted([fixture.Goal.Id]);

        Assert.Equal(WorkTaskStatus.Failed, fixture.Developer.Status);
        Assert.Equal(GoalLifecycleState.Failed, GoalLifecycle.ResolveState(fixture.Goal));
        var stored = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, stored.Status);
        Assert.StartsWith(outcomePrefix + " to goal ", stored.Outcome);
        var detail = $"blocked by Failed Developer task {fixture.Developer.Id.Value} ({rule}); {guidance}";
        Assert.Equal(
            $"{outcomePrefix} to goal {fixture.Goal.Id.Value[..8]} task {fixture.Tester.Id.Value[..8]}. {detail}",
            stored.Outcome);

        var record = Assert.Single(fixture.BlockedRecords(intent));
        var applied = record.OperatorIntentApplied!;
        Assert.Equal("applied-blocked", applied.Outcome);
        Assert.Equal($"operator-intent:{intent.Id} outcome=AppliedBlocked; {detail}", record.Message);
        var blocker = applied.BlockedBy!;
        Assert.Equal(AgentRole.Developer, blocker.Role);
        Assert.Equal(fixture.Developer.Id.Value, blocker.TaskId);
        Assert.Equal(rule, blocker.Reason);
        Assert.Equal(1, blocker.TaskNumber);
    }

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
