using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private kernel and temporary store, with no process or git calls.
public sealed class DispatchStartFailureEvidenceTests
{
    [Theory]
    [InlineData("absent")]
    [InlineData("earlier-dispatch")]
    [InlineData("other-task")]
    [InlineData("malformed")]
    public void UnprovenFailurePreservesPreparationDuringTickAndCancel(string evidence)
    {
        var f = new DispatchStartRecoveryTestFixture(solo: false);
        try
        {
            var dispatch = f.Prepare();
            if (evidence == "earlier-dispatch")
            {
                DispatchStartFailureEvidence.Record(f.Kernel, f.GoalId, f.TaskId, dispatch.DispatchedAt, "Older start failed");
                f.Kernel.DiscardUnstartedPreparedDispatch(f.GoalId, f.TaskId, dispatch.DispatchedAt, "Replace preparation");
                dispatch = f.Prepare();
            }
            if (evidence == "other-task")
            {
                var other = f.Kernel.AddTask(f.GoalId, AgentRole.Developer, "Other task");
                f.Kernel.RecordTaskNote(f.GoalId, other.Id, "DISPATCH_START_FAILURE " +
                    JsonSerializer.Serialize(new DispatchStartFailureEvidence(dispatch.DispatchedAt, "Other start failed")));
            }
            if (evidence == "malformed")
                f.Kernel.RecordTaskNote(f.GoalId, f.TaskId, "DISPATCH_START_FAILURE {malformed}");
            f.Restart();
            Assert.False(NeverStartedDispatchReconciliation.CanRecover(f.Goal, f.Task));
            var starts = 0;
            var driver = ConductorDriverTests.MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                startRecordedDispatches: _ => { starts++; return DispatchStartOutcome.Deferred("Preparation remains owned by start path"); });
            driver.BeginTick(f.Kernel, 1);

            driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive);

            Assert.Equal(1, starts);
            var store = new CancelDispatchIntentTestStore();
            var context = new CliExecutionContext(f.Kernel, f.Workspace, new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), f.Goal);
            var error = Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.ExecuteCancelDispatch(
                ["cancel-dispatch", "1"], context, store, _ => throw new InvalidOperationException("No process exists")));
            Assert.Contains("has no background process to cancel", error.Message);
            var payload = new CancelDispatchOperatorIntentPayload(0, dispatch.DispatchedAt,
                BackgroundDispatchRunner.BuildDispatchId(f.GoalId, f.TaskId, dispatch));
            Assert.False(NeverStartedDispatchReconciliation.ApplyCancel(f.Kernel, f.Goal, f.Task, payload));
            Assert.Equal(WorkTaskStatus.Running, f.Task.Status);
            Assert.Equal(dispatch.DispatchedAt, f.Task.LastDispatch!.DispatchedAt);
            Assert.Null(f.Task.LastProcess);
        }
        finally { Directory.Delete(f.Root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedStartFailureSurvivesReloadAndAllowsRecovery(bool cancel)
    {
        var f = new DispatchStartRecoveryTestFixture(solo: false);
        try
        {
            var dispatch = f.Prepare();
            // A recorded failure survived, but cleanup did not: the restart reconciliation window.
            DispatchStartFailureEvidence.Record(f.Kernel, f.GoalId, f.TaskId, dispatch.DispatchedAt, "Worker start failed");
            f.Restart();
            Assert.True(NeverStartedDispatchReconciliation.CanRecover(f.Goal, f.Task));

            if (cancel)
            {
                var payload = new CancelDispatchOperatorIntentPayload(0, dispatch.DispatchedAt,
                    BackgroundDispatchRunner.BuildDispatchId(f.GoalId, f.TaskId, dispatch));
                Assert.True(NeverStartedDispatchReconciliation.ApplyCancel(f.Kernel, f.Goal, f.Task, payload));
            }
            else
            {
                var driver = ConductorDriverTests.MakeDriver();
                driver.BeginTick(f.Kernel, 1);
                Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
            }

            Assert.Equal(WorkTaskStatus.Assigned, f.Task.Status);
            Assert.Null(f.Task.LastDispatch);
            Assert.Contains(f.Goal.Timeline, entry => entry.TaskId == f.TaskId &&
                entry.Kind == ProgressKind.TaskNote && entry.Message.Contains("reconciled a never-started dispatch"));
        }
        finally { Directory.Delete(f.Root, recursive: true); }
    }

    [Fact]
    public void AdmissionAbortPersistsFailureForItsExactDispatch()
    {
        var f = new DispatchStartRecoveryTestFixture();
        try
        {
            var dispatch = f.Prepare();
            const string text = "Retry admission failed";
            Assert.Throws<AbortedDispatchStartException>(() => AbortedDispatchStartRecovery.Admit(
                f.Kernel, f.GoalId, f.TaskId, checkpointCommitted: true,
                () => throw new InvalidOperationException(text)));
            f.Restart();

            var note = Assert.Single(f.Goal.Timeline.Where(entry => entry.TaskId == f.TaskId &&
                entry.Kind == ProgressKind.TaskNote && entry.Message.StartsWith("DISPATCH_START_FAILURE ", StringComparison.Ordinal)));
            var failure = JsonSerializer.Deserialize<DispatchStartFailureEvidence>(note.Message["DISPATCH_START_FAILURE ".Length..]);
            Assert.Equal(dispatch.DispatchedAt, failure!.DispatchedAt);
            Assert.Equal(text, failure.FailureText);
            Assert.Equal(WorkTaskStatus.Assigned, f.Task.Status);
            Assert.Null(f.Task.LastDispatch);
        }
        finally { Directory.Delete(f.Root, recursive: true); }
    }
}
