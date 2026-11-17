using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorMaxDurationInFlightKind
{
    AcceptanceAttempt,
    WorkerDispatch
}

internal sealed record ConductorMaxDurationInFlightAttempt(
    string AttemptId,
    IReadOnlyList<string> GoalIds,
    DateTimeOffset StartedAt,
    ConductorMaxDurationInFlightKind Kind = ConductorMaxDurationInFlightKind.AcceptanceAttempt);

internal enum ConductorMaxDurationStopVerdictKind
{
    Stop,
    Defer,
    StopExpired
}

internal sealed record ConductorMaxDurationStopVerdict(
    ConductorMaxDurationStopVerdictKind Kind,
    IReadOnlyList<ConductorMaxDurationInFlightAttempt> Attempts);

internal static class ConductorMaxDurationStopDeferral
{
    internal static ConductorMaxDurationStopVerdict Decide(
        DateTimeOffset utcNow,
        DateTimeOffset deferralStartedAt,
        IReadOnlyList<ConductorMaxDurationInFlightAttempt> attempts,
        TimeSpan ceiling,
        Exception? snapshotFailure = null)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        var ordered = attempts.OrderBy(attempt => attempt.AttemptId, StringComparer.Ordinal).ToArray();
        var elapsed = utcNow - deferralStartedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (snapshotFailure is not null)
        {
            return elapsed < ceiling
                ? new ConductorMaxDurationStopVerdict(ConductorMaxDurationStopVerdictKind.Defer, ordered)
                : new ConductorMaxDurationStopVerdict(ConductorMaxDurationStopVerdictKind.StopExpired, ordered);
        }

        if (ordered.Length == 0)
        {
            return new ConductorMaxDurationStopVerdict(ConductorMaxDurationStopVerdictKind.Stop, ordered);
        }

        return elapsed < ceiling
            ? new ConductorMaxDurationStopVerdict(ConductorMaxDurationStopVerdictKind.Defer, ordered)
            : new ConductorMaxDurationStopVerdict(ConductorMaxDurationStopVerdictKind.StopExpired, ordered);
    }

    internal static string FormatDeferredEvent(
        int tick,
        DateTimeOffset utcNow,
        DateTimeOffset deferralStartedAt,
        TimeSpan ceiling,
        IReadOnlyList<ConductorMaxDurationInFlightAttempt> attempts,
        Exception? snapshotFailure = null)
    {
        var acceptanceAttempts = attempts
            .Where(attempt => attempt.Kind == ConductorMaxDurationInFlightKind.AcceptanceAttempt)
            .ToArray();
        return $"LOOP_STOP_DEFERRED tick={tick} reason=max-duration " +
            $"deferral_started_at={deferralStartedAt:O} ceiling_ms={Math.Max(0L, (long)ceiling.TotalMilliseconds)} " +
            $"attempts={string.Join(',', acceptanceAttempts.Select(attempt => attempt.AttemptId))} " +
            $"attempt_elapsed_ms={string.Join(',', acceptanceAttempts.Select(attempt => $"{attempt.AttemptId}:{Math.Max(0L, (long)(utcNow - attempt.StartedAt).TotalMilliseconds)}"))} " +
            $"attempt_started_at={string.Join(',', acceptanceAttempts.Select(attempt => $"{attempt.AttemptId}:{attempt.StartedAt:O}"))}" +
            FormatWorkerDetail(attempts) +
            (snapshotFailure is null ? string.Empty : $" inflightStateUnavailable=true error={snapshotFailure.GetType().Name}");
    }

    internal static string FormatStopDetail(
        TimeSpan maxDuration,
        ConductorMaxDurationStopVerdict verdict,
        Exception? snapshotFailure = null)
    {
        var detail = $"seconds={(int)maxDuration.TotalSeconds}";
        if (verdict.Kind == ConductorMaxDurationStopVerdictKind.StopExpired)
        {
            detail += $" deferralExpired=true attempts={string.Join(',', verdict.Attempts
                .Where(attempt => attempt.Kind == ConductorMaxDurationInFlightKind.AcceptanceAttempt)
                .Select(attempt => attempt.AttemptId))}";
            detail += FormatWorkerDetail(verdict.Attempts);
        }

        if (snapshotFailure is not null)
        {
            return $"{detail} inflightStateUnavailable=true error={snapshotFailure.GetType().Name}";
        }

        return detail;
    }

    private static string FormatWorkerDetail(IReadOnlyList<ConductorMaxDurationInFlightAttempt> attempts)
    {
        var workerIds = attempts
            .Where(attempt => attempt.Kind == ConductorMaxDurationInFlightKind.WorkerDispatch)
            .Select(attempt => attempt.AttemptId)
            .ToArray();
        return workerIds.Length == 0 ? string.Empty : $" workers={string.Join(',', workerIds)}";
    }
}

internal static class ConductorMaxDurationSnapshotBuilder
{
    internal sealed record Snapshot(
        IReadOnlyList<ConductorMaxDurationInFlightAttempt> Attempts,
        Exception? Failure = null);

    internal static Snapshot Build(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        string? onlyGoalId,
        IReadOnlySet<string>? trackedAttemptIds)
    {
        var snapshot = BuildMaxDurationAcceptanceSnapshot(kernel, driver, trackedAttemptIds);
        return onlyGoalId is null
            ? snapshot
            : snapshot with
            {
                Attempts = snapshot.Attempts
                    .Concat(BuildMaxDurationWorkerSnapshot(kernel, onlyGoalId, trackedAttemptIds))
                    .ToArray()
            };
    }

    private static IReadOnlyList<ConductorMaxDurationInFlightAttempt> BuildMaxDurationWorkerSnapshot(
        AgentOrchestratorKernel kernel,
        string onlyGoalId,
        IReadOnlySet<string>? trackedAttemptIds) =>
        kernel.Goals
            .Where(goal => goal.Id.Value == onlyGoalId)
            .SelectMany(goal => goal.Tasks)
            .Where(task => task.LastProcess is { } process &&
                (process.IsRunning || task.CompletionBuildEvidence is { IsPending: true } ||
                 DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, process)))
            .Select(task => new ConductorMaxDurationInFlightAttempt(
                $"worker:{task.Id.Value}:{task.LastProcess!.ProcessId}",
                [onlyGoalId],
                task.LastProcess!.StartedAt,
                ConductorMaxDurationInFlightKind.WorkerDispatch))
            .Where(attempt => trackedAttemptIds is null || trackedAttemptIds.Contains(attempt.AttemptId))
            .ToArray();

    private static Snapshot BuildMaxDurationAcceptanceSnapshot(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        IReadOnlySet<string>? trackedAttemptIds)
    {
        try
        {
            var goalIds = kernel.Goals.Select(goal => goal.Id.Value).ToArray();
            var parallelAttempts = trackedAttemptIds is null
                ? driver.ParallelAcceptanceAttemptCoordinator.GetCapacityReservingAttempts(goalIds)
                : driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts(goalIds)
                    .Where(attempt => trackedAttemptIds.Contains(attempt.AttemptId))
                    .ToArray();
            var attempts = parallelAttempts
                .Select(attempt => new ConductorMaxDurationInFlightAttempt(
                    attempt.AttemptId,
                    [attempt.GoalId],
                    attempt.StartedAt))
                .ToList();
            attempts.AddRange(driver.GetActiveAcceptanceCohortCapacity().ActiveRoots
                .Where(root => trackedAttemptIds is null || trackedAttemptIds.Contains(root.Key))
                .Select(root => new ConductorMaxDurationInFlightAttempt(
                    root.Key,
                    root.MemberGoalIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                    root.StartedAt)));
            return new Snapshot(attempts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new Snapshot([], ex);
        }
    }
}
