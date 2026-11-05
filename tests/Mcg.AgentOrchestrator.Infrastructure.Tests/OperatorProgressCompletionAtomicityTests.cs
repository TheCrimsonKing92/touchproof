using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixtures own all mutable state and persistence paths.
public sealed class OperatorProgressCompletionAtomicityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptFailure_RestoresScopeBeforeLaterCheckpoint(bool afterReceipt)
    {
        using var fixture = new OperatorProgressCompletionFixture();
        var unrelated = fixture.Kernel.CreateGoal("Unrelated", [new(TaskId.New(), "Other", AgentRole.Developer)]);
        var otherBefore = JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(unrelated.Id));
        var before = JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(fixture.GoalId));
        var intent = await fixture.Enqueue(fixture.Completion());
        var observedCompletion = false;
        var failure = Assert.Throws<InvalidOperationException>(() => OperatorProgressCompletion.Apply(
            fixture.Kernel, fixture.Goal, fixture.Task, intent, fixture.Completion(), null, receipt =>
            {
                Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
                observedCompletion = true;
                if (afterReceipt) fixture.Kernel.RecordTaskVerification(fixture.GoalId, fixture.TaskId, receipt);
                fixture.Kernel.RequestHumanInputDeduplicated(fixture.GoalId, fixture.TaskId,
                    "Input created by failing operation", HumanWaitKind.SpecClarification);
                throw new InvalidOperationException("injected-receipt-failure");
            }));
        Assert.Equal("injected-receipt-failure", failure.Message);
        Assert.True(observedCompletion);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(fixture.GoalId)));
        Assert.Equal(otherBefore, JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(unrelated.Id)));
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        fixture.Kernel.ReportTaskProgress(unrelated.Id, unrelated.Tasks.Single().Id,
            WorkTaskStatus.Running, "Later checkpoint");
        await fixture.Save();
        await fixture.Reload();
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        Assert.Empty(fixture.Task.VerificationHistory);
        Assert.DoesNotContain(fixture.Goal.Timeline, entry => entry.Kind == ProgressKind.TaskCompleted);
        Assert.Equal(WorkTaskStatus.Running, fixture.Kernel.GetGoal(unrelated.Id).Tasks.Single().Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableMarker_CrashAndLaterRetry_RecoverWithoutReapplying(bool retry)
    {
        using var fixture = new OperatorProgressCompletionFixture();
        var intent = await fixture.Enqueue(fixture.Completion());
        Assert.True(fixture.Apply().MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Claimed, (await fixture.Store.GetAsync(intent.Id))!.Status);
        await fixture.Save(); // Crash before CompletePersisted.
        await fixture.Reload();
        if (retry)
        {
            fixture.Kernel.RetryTaskWithAuthoritativeFeedback(fixture.GoalId, fixture.TaskId,
                "New round", RetryCause.ContractClarification, invalidateDownstream: true);
            await fixture.Save();
            await fixture.Reload();
            Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        }
        var before = JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(fixture.GoalId));
        var recovery = fixture.Apply();
        Assert.False(recovery.MutatedGoalState);
        Assert.Contains("result=applied-recovered", Assert.Single(recovery.ProgressLines));
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(fixture.GoalId)));
        Assert.Single(fixture.Task.VerificationHistory);
        Assert.Single(fixture.Goal.Timeline, item => item.TaskId == fixture.TaskId && item.Kind == ProgressKind.TaskCompleted);
        var stored = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, stored.Status);
        var replay = await fixture.Store.EnqueueAsync(intent with { Id = Guid.NewGuid().ToString("N") });
        Assert.Equal(stored.Id, replay.Id);
        Assert.Equal(stored.Outcome, replay.Outcome);
        Assert.False(fixture.Apply().MutatedGoalState);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(fixture.GoalId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.EnqueueAsync(intent with
        {
            Id = Guid.NewGuid().ToString("N"),
            PayloadJson = JsonSerializer.Serialize(fixture.Completion("Different evidence"), JsonSerializerOptions.Web)
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctRequest_AlreadyCompleted_RefusesAndKeepsVerification(bool receipt)
    {
        using var fixture = new OperatorProgressCompletionFixture();
        fixture.Kernel.ReportTaskProgress(fixture.GoalId, fixture.TaskId, WorkTaskStatus.Completed, "Earlier completion");
        if (receipt)
            fixture.Kernel.RecordTaskVerification(fixture.GoalId, fixture.TaskId,
                ManualVerificationRecorder.Create(true, "Earlier attestation", fixture.Root, DateTimeOffset.UtcNow));
        await fixture.Save();
        var before = JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(fixture.GoalId));
        var intent = await fixture.Enqueue(fixture.Completion());
        Assert.False(fixture.Apply().MutatedGoalState);
        var rejected = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
        Assert.Contains($"verify-manual {fixture.GoalId.Value[..8]} 1 passed <note>", rejected.Outcome);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Kernel.ExportGoalSnapshot(fixture.GoalId)));
        await fixture.Save();
        await fixture.Reload();
        Assert.Equal(receipt ? 1 : 0, fixture.Task.VerificationHistory.Count);
    }
}
