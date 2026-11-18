using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// A receipt describes one already reaped dispatch generation; replay never performs process I/O.
internal sealed record GoalWorkerTerminationReceipt(
    TaskId TaskId, TaskProcessRecord CancelledProcess,
    CancellationCandidateEvidence CandidateEvidence, IReadOnlyList<string> TaskNotes,
    CompletionBuildEvidenceOperation? CompletionBuildEvidence = null,
    GoalWorkerTerminationOutcome Outcome = GoalWorkerTerminationOutcome.TerminationUnconfirmed);

internal static class GoalWorkerTerminationTextView
{
    internal static void PrintParkOutcome(string confirmation, IReadOnlyList<GoalWorkerTerminationReceipt> receipts,
        int? resolvedHumanWaits, int resolvedAttentionItems)
    {
        var cancelledCount = receipts.Count(receipt => receipt.Outcome == GoalWorkerTerminationOutcome.ConfirmedTerminated);
        var unconfirmed = receipts.Where(receipt => receipt.Outcome != GoalWorkerTerminationOutcome.ConfirmedTerminated).ToArray();
        Console.WriteLine(confirmation);
        Console.WriteLine($"Cancelled running dispatches: {cancelledCount}");
        if (unconfirmed.Length > 0)
        {
            Console.WriteLine($"Termination unconfirmed: {unconfirmed.Length}");
            foreach (var receipt in unconfirmed)
                Console.WriteLine($"  {receipt.TaskId.Value[..8]} (pid {receipt.CancelledProcess.ProcessId})");
        }
        Console.WriteLine($"Resolved human waits: {resolvedHumanWaits}");
        Console.WriteLine($"Resolved attention items: {resolvedAttentionItems}");
        if (unconfirmed.Length > 0)
            Console.WriteLine("Goal worktree is not safe to mutate while those workers may be running.");
    }
}

internal static class GoalWorkerTermination
{
    private static readonly TimeSpan ConfirmationBudget = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<Func<GoalSnapshot, TaskId, GoalWorkerTerminationReceipt>?> Override = new();

    internal static IDisposable Push(Func<GoalSnapshot, TaskId, GoalWorkerTerminationReceipt> terminate)
    {
        var previous = Override.Value;
        Override.Value = terminate;
        return new RestoreAction(() => Override.Value = previous);
    }

    internal static IReadOnlyList<GoalWorkerTerminationReceipt> Terminate(GoalSnapshot snapshot, string disposition)
    {
        var receipts = new List<GoalWorkerTerminationReceipt>();
        try
        {
            var selection = new AgentOrchestratorKernel();
            selection.ReplaceGoalWithSnapshot(snapshot);
            foreach (var task in selection.GetGoal(new GoalId(snapshot.Id)).Tasks
                .Where(task => task.LastProcess is { IsRunning: true } || task.CompletionBuildEvidence is { IsPending: true }))
            {
                receipts.Add((Override.Value ?? TerminateDispatch)(snapshot, task.Id));
            }
            return receipts;
        }
        catch (Exception ex) when (receipts.Count > 0)
        {
            throw DidNotCommit(disposition, receipts, ex);
        }
    }

    private static GoalWorkerTerminationReceipt TerminateDispatch(GoalSnapshot snapshot, TaskId taskId)
    {
        // The existing runner owns reap, resource accounting, and planner sample delivery. Its
        // kernel writes are isolated here and become durable only when replayed in the transaction.
        var isolated = new AgentOrchestratorKernel();
        isolated.ReplaceGoalWithSnapshot(snapshot);
        var goalId = new GoalId(snapshot.Id);
        var goal = isolated.GetGoal(goalId);
        var task = isolated.GetTask(goalId, taskId);
        var original = task.LastProcess!;
        var timelineCount = goal.Timeline.Count;
        var captures = CaptureRecordedProcesses(original);
        TaskProcessRecord cancelled;
        GoalWorkerTerminationOutcome outcome;
        try
        {
            cancelled = new BackgroundDispatchRunner().CancelLatestProcess(isolated, goalId, taskId);
            outcome = ConfirmExit(captures, ConfirmationBudget);
        }
        finally
        {
            foreach (var capture in captures) capture.Dispose();
        }
        var candidate = CancellationCandidateEvidenceClassifier.Classify(
            task, original, goalId, new DispatchWorktreeCommitter());
        var notes = goal.Timeline.Skip(timelineCount)
            .Where(item => item.TaskId == taskId && item.Kind == ProgressKind.TaskNote &&
                !item.Message.StartsWith("CANCELLATION_CANDIDATE_EVIDENCE", StringComparison.Ordinal))
            .Select(item => item.Message).ToArray();
        return new(taskId, cancelled, candidate, notes, task.CompletionBuildEvidence, outcome);
    }

    internal enum RecordedProcessCaptureKind { Absent, Held, Unverifiable }

    internal sealed record RecordedProcessCapture(int ProcessId, RecordedProcessCaptureKind Kind,
        Func<TimeSpan, bool>? WaitForExit = null, IDisposable? Handle = null) : IDisposable
    {
        public void Dispose()
        {
            try { Handle?.Dispose(); }
            catch { /* Handle cleanup must not prevent the park commit. */ }
        }
    }

    internal static IReadOnlyList<RecordedProcessCapture> CaptureRecordedProcesses(TaskProcessRecord original,
        Func<int, Process>? openProcess = null) => original.TrackedProcessIds.Append(original.ProcessId).Distinct()
        .Select(pid => CaptureRecordedProcess(original, pid, openProcess ?? Process.GetProcessById)).ToArray();

    private static RecordedProcessCapture CaptureRecordedProcess(TaskProcessRecord original, int pid,
        Func<int, Process> openProcess)
    {
        if (pid <= 0) return new(pid, RecordedProcessCaptureKind.Unverifiable);
        Process process;
        try { process = openProcess(pid); }
        catch (ArgumentException) { return new(pid, RecordedProcessCaptureKind.Absent); }
        catch { return new(pid, RecordedProcessCaptureKind.Unverifiable); }

        var kind = RecordedProcessCaptureKind.Unverifiable;
        var retained = false;
        try
        {
            // Force the handle open before cancellation; a later pid lookup could see a new owner.
            _ = process.SafeHandle;
            if (process.HasExited)
                kind = RecordedProcessCaptureKind.Absent;
            else
            {
                var startedAt = new DateTimeOffset(process.StartTime);
                var reused = startedAt < (original.ProcessIdentityStartedAt ?? original.StartedAt).AddSeconds(-5);
                if (pid == original.ProcessId)
                {
                    if (original.ProcessIdentityStartedAt is { } recordedStart)
                        reused |= (startedAt - recordedStart).Duration() > TimeSpan.FromSeconds(1);
                    if (!string.IsNullOrWhiteSpace(original.ProcessIdentityImagePath))
                    {
                        var imagePath = process.MainModule?.FileName;
                        if (string.IsNullOrWhiteSpace(imagePath))
                            return new(pid, RecordedProcessCaptureKind.Unverifiable);
                        // Image providers can disagree for the same live process. A mismatch
                        // cannot establish that the recorded process has exited or its pid was reused.
                        if (!Path.GetFullPath(imagePath).Equals(
                            Path.GetFullPath(original.ProcessIdentityImagePath),
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                            return new(pid, RecordedProcessCaptureKind.Unverifiable);
                    }
                }
                if (reused)
                    kind = RecordedProcessCaptureKind.Absent;
                else
                {
                    retained = true;
                    return new(pid, RecordedProcessCaptureKind.Held, process.WaitForExit, process);
                }
            }
        }
        catch { /* Inaccessible or unreadable process evidence cannot establish exit. */ }
        finally
        {
            if (!retained)
            {
                try { process.Dispose(); }
                catch { /* Handle cleanup must not prevent the park commit. */ }
            }
        }
        return new(pid, kind);
    }

    internal static GoalWorkerTerminationOutcome ConfirmExit(IReadOnlyList<RecordedProcessCapture> captures,
        TimeSpan budget, TimeProvider? timeProvider = null)
    {
        try
        {
            if (captures.Count == 0) return GoalWorkerTerminationOutcome.TerminationUnconfirmed;
            var clock = timeProvider ?? TimeProvider.System;
            var started = clock.GetTimestamp();
            foreach (var capture in captures)
            {
                if (capture.Kind == RecordedProcessCaptureKind.Absent) continue;
                if (capture.Kind != RecordedProcessCaptureKind.Held || capture.WaitForExit is null)
                    return GoalWorkerTerminationOutcome.TerminationUnconfirmed;
                var remaining = budget - clock.GetElapsedTime(started);
                if (!capture.WaitForExit(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
                    return GoalWorkerTerminationOutcome.TerminationUnconfirmed;
            }
            return GoalWorkerTerminationOutcome.ConfirmedTerminated;
        }
        catch
        {
            return GoalWorkerTerminationOutcome.TerminationUnconfirmed;
        }
    }

    internal static void Replay(AgentOrchestratorKernel kernel, GoalId goalId,
        IReadOnlyList<GoalWorkerTerminationReceipt> receipts)
    {
        foreach (var receipt in receipts)
        {
            var current = kernel.GetTask(goalId, receipt.TaskId).LastProcess;
            var cancelled = receipt.CancelledProcess;
            if (current is null || current.ProcessId != cancelled.ProcessId || current.StartedAt != cancelled.StartedAt ||
                current.Command != cancelled.Command || current.WorkingDirectory != cancelled.WorkingDirectory)
            {
                throw new InvalidOperationException($"Task {receipt.TaskId.Value[..8]} dispatch generation changed after termination.");
            }
            if (!current.IsRunning && kernel.GetTask(goalId, receipt.TaskId).CompletionBuildEvidence is not { IsPending: true })
            {
                if (current.WasCancelled) continue;
                throw new InvalidOperationException($"Task {receipt.TaskId.Value[..8]} completed after termination preparation.");
            }
            if (receipt.CompletionBuildEvidence is { } build)
                kernel.RecordCompletionBuildEvidence(goalId, receipt.TaskId, build);
            kernel.RecordTaskProcessCancelled(goalId, receipt.TaskId, cancelled, receipt.CandidateEvidence);
            foreach (var note in receipt.TaskNotes) kernel.RecordTaskNote(goalId, receipt.TaskId, note);
        }
    }

    internal static InvalidOperationException DidNotCommit(string disposition,
        IReadOnlyList<GoalWorkerTerminationReceipt> receipts, Exception cause) => new(
        $"stop --as {disposition} did not commit after terminating workers: " +
        string.Join(", ", receipts.Select(receipt =>
            $"task {receipt.TaskId.Value[..8]} pid {receipt.CancelledProcess.ProcessId}")) + ".", cause);

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
