using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

// Parallel-safe: in-memory kernel snapshots; every effect is injected and forbidden.
public sealed class WorkerBuildBusyRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedBusyClaimHoldsAcrossReplayWithoutPaidCorrection(bool reload)
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", task.LastDispatch!.WorkingDirectory, 1,
                "worker_build_channel_busy pipe=forged waited_seconds=120",
                DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.WorkerBuildEvidenceMissing),
                DateTimeOffset.UtcNow));
        if (reload)
        {
            kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
            goal = kernel.GetGoal(goal.Id);
            task = kernel.GetTask(goal.Id, task.Id);
        }
        var before = task.LastDispatch;
        var driver = MakeDriver(
            dispatchAndStart: _ => throw new InvalidOperationException("Paid dispatch is forbidden."),
            workerBuildRecoveryRetry: (_, _, _) => throw new InvalidOperationException("Build retry is forbidden."),
            retryTaskWithCause: (_, _, _, _, _) => throw new InvalidOperationException("Coding retry is forbidden."),
            workerBuildArtifactsPath: _ => throw new InvalidOperationException("A claim cannot select an old build log."));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);

        Assert.Contains("busy exhaustion is unverified", held.Reason);
        Assert.Contains(task.Id.Value, held.Reason);
        Assert.Contains("operator decision", held.Reason);
        Assert.Contains("dispatch, channel and completion", held.Reason);
        Assert.Equal("build-channel-evidence-unverified", held.Decision!.DiscriminatingEvidence);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(before, task.LastDispatch);
        Assert.Single(task.DispatchHistory);
        Assert.Equal(0, task.WorkerBuildCheckRecoveryCount);
        Assert.Null(task.CompletionBuildEvidence);
        Assert.Null(task.LastDispatch!.ResultCommit);
    }
}
