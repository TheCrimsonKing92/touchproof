using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class RetryAdmissionContinuationBaselineTests
{
    [Theory]
    [InlineData(null, PaidRouteClassification.NonPaid, 1)]
    [InlineData(null, PaidRouteClassification.Paid, 0)]
    [InlineData(null, PaidRouteClassification.Paid, 1)]
    [InlineData("no-completion", PaidRouteClassification.NonPaid, 1)]
    [InlineData("no-exit-code", PaidRouteClassification.NonPaid, 1)]
    [InlineData("no-process", PaidRouteClassification.NonPaid, 1)]
    [InlineData("completion-at-dispatch", PaidRouteClassification.NonPaid, 1)]
    [InlineData("completion-after-dispatch", PaidRouteClassification.NonPaid, 1)]
    public async Task ClarifiedContinuationAdmitsOnlySettledImmediatePredecessor(string? invalid, PaidRouteClassification route, int exitCode)
    {
        var f = new DispatchStartRecoveryTestFixture();
        var marker = f.Task.LatestRetryAt;
        var first = f.Prepare(route: route);
        f.SettleAndAnswer();
        Assert.Equal(marker, f.Task.LatestRetryAt);
        var replay = f.Kernel.ExportGoalSnapshot(f.GoalId).Tasks.Single();
        replay = replay with { LastProcess = replay.LastProcess! with { ExitCode = exitCode } };
        var prepared = f.Prepare(route: route) with { RetryContextFingerprint = f.Fingerprint("clarified-candidate", route) };
        replay = invalid switch
        {
            "no-completion" => replay with { LastProcess = replay.LastProcess! with { CompletedAt = null } },
            "no-exit-code" => replay with { LastProcess = replay.LastProcess! with { ExitCode = null } },
            "no-process" => replay with { LastProcess = null },
            "completion-at-dispatch" => replay with { LastProcess = replay.LastProcess! with { CompletedAt = prepared.DispatchedAt } },
            "completion-after-dispatch" => replay with { LastProcess = replay.LastProcess! with { CompletedAt = prepared.DispatchedAt.AddTicks(1) } },
            _ => replay
        };
        f.Save();
        var reservation = RetryAdmissionReservationStore.TryReserveAsync(f.Workspace.SqliteStatePath,
            f.GoalId, f.TaskId, prepared.RetryContextFingerprint!, prepared.PaidRoute, f.Task.PendingRetryCause,
            prepared, f.Time.UtcNow, "continuation", f.Time.UtcNow.AddMinutes(1),
            retryMarkerAt: marker, retryRoundKind: f.Task.PendingRetryRoundKind,
            retryMessage: f.Goal.Timeline.Last(entry => entry.Kind == ProgressKind.TaskRetried).Message,
            retryReplayTask: replay);

        if (invalid is not null)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reservation);
            Assert.Contains("does not describe the exact post-retry, pre-dispatch state", error.Message);
            return;
        }
        var result = Assert.IsType<RetryAdmissionSnapshotResult>(await reservation);
        Assert.True(result.Admission.AllowsProcessStart);
        var task = result.Snapshot.Tasks.Single();
        Assert.Equal(prepared.DispatchedAt, task.LastDispatch!.DispatchedAt);
        Assert.Contains(task.DispatchHistory!, dispatch => dispatch.DispatchedAt == first.DispatchedAt);
        Assert.Equal(marker, task.LatestRetryAt);
    }
}
