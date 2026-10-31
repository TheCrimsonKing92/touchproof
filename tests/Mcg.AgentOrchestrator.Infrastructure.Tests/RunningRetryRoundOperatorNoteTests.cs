using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its SQLite files; process records are synthetic.
public sealed class RunningRetryRoundOperatorNoteTests
{
    [Xunit.Fact]
    public async Task RunningRetry_QueuesNoteWithoutSuppression_AndDeliversNextRound()
    {
        using var scenario = new AcceptanceRetryRoutingScenario(holdSeconds: 0, maxRetries: 1);
        scenario.FailGate();
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        var dispatch = scenario.Task.LastDispatch;
        var process = new TaskProcessRecord(4242, "test-worker", scenario.Workspace.ExecutionDirectory,
            "out", "err", "exit", scenario.Now, null, null);
        scenario.Kernel.RecordTaskProcessStarted(scenario.Goal.Id, scenario.Task.Id, process);
        var reference = scenario.PrepareAdmission();
        var retryAt = scenario.Task.LatestRetryAt;
        var feedback = scenario.Task.CriterionRetryFeedback.ToArray();
        var executed = 0;
        TaskIntentAdmission.ExecuteAuthorized(reference, "before-note", () => executed++);
        scenario.Now = scenario.Now.AddSeconds(1);
        const string instruction = "Keep the attempt-creation manifest guard.";

        var intent = await scenario.Route(instruction, "running-receipt");

        Xunit.Assert.Equal(OperatorIntentStatus.Applied, intent.Status);
        Xunit.Assert.Contains("queued for the next round", intent.Outcome);
        Xunit.Assert.True(OperatorRoutingNote.IsQueuedOutcome(intent.Outcome));
        Xunit.Assert.Equal(WorkTaskStatus.Running, scenario.Task.Status);
        Xunit.Assert.Equal(process, scenario.Task.LastProcess);
        Xunit.Assert.Equal(dispatch, scenario.Task.LastDispatch);
        Xunit.Assert.Equal(retryAt, scenario.Task.LatestRetryAt);
        Xunit.Assert.Equal(feedback, scenario.Task.CriterionRetryFeedback);
        Xunit.Assert.Equal(1, scenario.Task.CriterionRetryCount);
        Xunit.Assert.Equal(scenario.Policy.MaxCriterionRetries, scenario.Goal.AutomaticAcceptanceRetryCount);
        Xunit.Assert.Equal(1, scenario.DispatchCalls);
        var note = Xunit.Assert.Single(scenario.Task.OperatorRoutingNotes);
        Xunit.Assert.True(note.QueuedWhileRunning);
        Xunit.Assert.Null(note.DeliveredAt);
        Xunit.Assert.Equal("running-receipt", Xunit.Assert.Single(note.EvidenceReferences));
        Xunit.Assert.Contains(scenario.Goal.Timeline, entry => entry.Message.Contains(intent.Id, StringComparison.Ordinal));

        var observed = new List<TaskIntentAdmissionEvent>();
        TaskIntentAdmission.ExecuteAuthorized(reference, "after-note", () => executed++, observed.Add);
        Xunit.Assert.Equal(2, executed);
        Xunit.Assert.Equal("continued", Xunit.Assert.Single(observed).Kind);
        Xunit.Assert.Equal("Applied", observed[0].IntentStatus);
        Xunit.Assert.Equal(intent.Id, observed[0].IntentId);
        Xunit.Assert.Null(scenario.Store.GetSuppression("after-note"));

        await scenario.Recover();
        var recoveredProcess = Xunit.Assert.IsType<TaskProcessRecord>(scenario.Task.LastProcess);
        // Snapshots materialize the default tracked PID list; compare its contents
        // separately so recovery still proves every process field is preserved.
        Xunit.Assert.Equal(process.TrackedProcessIds, recoveredProcess.TrackedProcessIds);
        Xunit.Assert.Equal(process, recoveredProcess with { OwnedProcessIds = process.OwnedProcessIds });
        Xunit.Assert.Null(Xunit.Assert.Single(scenario.Task.OperatorRoutingNotes).DeliveredAt);
        scenario.Now = scenario.Now.AddSeconds(1);
        // This independent retry is authorized separately; the note did not schedule it
        // or bypass the exhausted automatic acceptance retry cap.
        scenario.Kernel.ReportTaskProgress(scenario.Goal.Id, scenario.Task.Id, WorkTaskStatus.Failed, "later failure");
        scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Task.Id, "Independent source correction",
            retryCause: RetryCause.NewSourceFinding);
        Xunit.Assert.Null(scenario.Task.AcceptanceRetryRoutingHold);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        var block = Xunit.Assert.Single(scenario.DeliveredFeedback[1]);
        Xunit.Assert.Contains(instruction, block);
        Xunit.Assert.Contains("running-receipt", block);
        Xunit.Assert.Contains($"submitted_at={intent.CreatedAt:O}", block);
        Xunit.Assert.Equal(block, scenario.Task.AcceptedRetryFeedback?.Message);
        Xunit.Assert.Equal(1, scenario.Goal.AutomaticAcceptanceRetryCount);
        Xunit.Assert.Equal(scenario.Now, Xunit.Assert.Single(scenario.Task.OperatorRoutingNotes).DeliveredAt);
    }

    [Xunit.Fact]
    public async Task QueuedNotesAndHoldRoute_AreDeliveredInOrder_OnceAcrossRecovery()
    {
        using var scenario = new AcceptanceRetryRoutingScenario(holdSeconds: 0);
        scenario.FailGate();
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        scenario.Now = scenario.Now.AddSeconds(1);
        var first = await scenario.Route("First queued instruction", "queued-first");
        scenario.Now = scenario.Now.AddSeconds(1);
        var second = await scenario.Route("Second queued instruction", "queued-second");
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, first.Status);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, second.Status);
        Xunit.Assert.True(OperatorRoutingNote.IsQueuedOutcome(first.Outcome));
        Xunit.Assert.True(OperatorRoutingNote.IsQueuedOutcome(second.Outcome));
        Xunit.Assert.Equal(1, scenario.DispatchCalls);

        scenario.Policy = scenario.Policy with { AutomaticRetryOperatorRoutingHoldSeconds = 180 };
        scenario.Now = scenario.Now.AddSeconds(1);
        scenario.FailGate();
        var deadline = scenario.Task.AcceptanceRetryRoutingHold!.Deadline;
        scenario.Now = scenario.Now.AddSeconds(1);
        var last = await scenario.Route("Last hold instruction", "hold-last");
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, last.Status);
        Xunit.Assert.False(OperatorRoutingNote.IsQueuedOutcome(last.Outcome));
        Xunit.Assert.Equal(2, scenario.Goal.AutomaticAcceptanceRetryCount);
        await scenario.Recover();
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        Xunit.Assert.Equal(1, scenario.DispatchCalls);
        scenario.Now = deadline;
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);

        var block = Xunit.Assert.Single(scenario.DeliveredFeedback[1]);
        var firstIndex = block.IndexOf("First queued instruction", StringComparison.Ordinal);
        var secondIndex = block.IndexOf("Second queued instruction", StringComparison.Ordinal);
        var lastIndex = block.IndexOf("Last hold instruction", StringComparison.Ordinal);
        Xunit.Assert.True(firstIndex >= 0 && firstIndex < secondIndex && secondIndex < lastIndex);
        Xunit.Assert.StartsWith("Later operator notes override earlier ones where they conflict.", block);
        Xunit.Assert.Contains("queued-first", block);
        Xunit.Assert.Contains("queued-second", block);
        Xunit.Assert.Contains("hold-last", block);
        Xunit.Assert.Contains(AcceptanceRetryRoutingScenario.AttemptId, block);
        Xunit.Assert.Contains(AcceptanceRetryRoutingScenario.Assertion, block);
        Xunit.Assert.All(scenario.Task.OperatorRoutingNotes, note => Xunit.Assert.NotNull(note.DeliveredAt));
        await scenario.Recover();
        Xunit.Assert.All(scenario.Task.OperatorRoutingNotes, note => Xunit.Assert.NotNull(note.DeliveredAt));

        scenario.Now = scenario.Now.AddSeconds(1);
        scenario.FailGate();
        scenario.Now = scenario.Task.AcceptanceRetryRoutingHold!.Deadline;
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        Xunit.Assert.Equal(scenario.AutomaticFeedback, scenario.DeliveredFeedback[2]);
        Xunit.Assert.DoesNotContain("instruction", string.Join("\n", scenario.DeliveredFeedback[2]));
        Xunit.Assert.Equal(3, scenario.Goal.AutomaticAcceptanceRetryCount);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task NonRetryRunningRound_RefusesRoute_EvenWithHistoricalRetry(bool historicalRetry)
    {
        using var scenario = new AcceptanceRetryRoutingScenario(holdSeconds: 0);
        if (historicalRetry)
        {
            scenario.FailGate();
            scenario.Now = scenario.Now.AddSeconds(1);
            // The same RetryCause is also used outside automatic acceptance retries.
            scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Task.Id, "Different retry kind",
                retryCause: RetryCause.CriterionEvidenceOwnerMismatch);
        }
        Xunit.Assert.Null(scenario.Task.AcceptanceRetryRoutingHold);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        var reference = scenario.PrepareAdmission();
        var dispatch = scenario.Task.LastDispatch;
        var intent = await scenario.Route("Not eligible for queued routing");
        Xunit.Assert.Equal(OperatorIntentStatus.Rejected, intent.Status);
        Xunit.Assert.Contains("task-not-retryable", intent.Outcome);
        Xunit.Assert.Equal(WorkTaskStatus.Running, scenario.Task.Status);
        Xunit.Assert.Equal(dispatch, scenario.Task.LastDispatch);
        Xunit.Assert.Empty(scenario.Task.OperatorRoutingNotes);
        var calls = 0;
        TaskIntentAdmission.ExecuteAuthorized(reference, "after-refusal", () => calls++);
        Xunit.Assert.Equal(1, calls);
    }
}
