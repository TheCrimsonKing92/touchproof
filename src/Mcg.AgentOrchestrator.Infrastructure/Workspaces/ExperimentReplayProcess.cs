using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Uses the same owned job/accounting implementation as acceptance. Sampling never claims
// completeness: exact job totals and observed executable lower bounds have separate validity.
internal static class ExperimentReplayProcess
{
    // Scoped observation lets contract tests synchronize with actual job membership, not elapsed time.
    private const int ObservationIntervalMilliseconds = 10;
    private sealed record ObservationHook(Action<OwnedProcessGroup, SafeFileHandle, int> Observer,
        Func<string, bool>? IncludeExecutable);
    private static readonly AsyncLocal<ObservationHook?> Observation = new();

    internal static IDisposable ObserveForTests(Action<OwnedProcessGroup, SafeFileHandle, int> observer,
        Func<string, bool>? includeExecutable = null)
    {
        var previous = Observation.Value;
        Observation.Value = new(observer, includeExecutable);
        return new ObservationScope(() => Observation.Value = previous);
    }

    private sealed class ObservationScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    internal sealed record Result(int? ExitCode, string State, string? Reason, string Priority, string PowerThrottling,
        ExperimentReplayResources? Resources, string StdoutPath, string StderrPath)
    {
        public ExitMembership? ExitInspection { get; init; }
    }

    internal sealed record MemberIdentity(int ProcessId, DateTimeOffset StartedAt, string ExecutablePath);
    internal sealed record ExitMembership(int RootProcessId, DateTime RootStartedAtUtc,
        IReadOnlyList<int> ReportedProcessIds, IReadOnlyList<MemberIdentity> LiveDescendants,
        IReadOnlyList<int> UnverifiedProcessIds);

    internal static async Task<Result> RunAsync(string executable, IReadOnlyList<string> arguments, string directory,
        string artifacts, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Replay accounting requires Windows job objects.");
        Directory.CreateDirectory(artifacts);
        var stdout = Path.Combine(artifacts, "stdout.txt");
        var stderr = Path.Combine(artifacts, "stderr.txt");
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(info.Environment, directory);
        // Each procedure receives an explicit fresh temp/fixture directory.
        var temp = Path.Combine(artifacts, "temp");
        Directory.CreateDirectory(temp);
        info.Environment["TEMP"] = temp;
        info.Environment["TMP"] = temp;
        var elapsed = Stopwatch.StartNew();
        using var pending = OwnedProcessGroup.StartSuspendedContainedRedirected(info);
        // This launcher assigns a no-breakaway job before returning the suspended root.
        var assignedBeforeResume = pending.Group.Contains(pending.Process.SafeHandle);
        if (!assignedBeforeResume) throw new InvalidOperationException("Suspended replay root is outside its contained job.");
        pending.Process.PriorityClass = ProcessPriorityClass.BelowNormal;
        var scheduling = ProcessSchedulingProbe.Read(pending.Process.Id);
        if (!scheduling.IsBelowNormal) throw new InvalidOperationException("Below-normal replay priority could not be established.");
        var observations = new Dictionary<(int, long), (string Image, bool BelowNormal)>();
        Observe(pending.Group, observations);
        var outputTask = DrainAsync(pending.StandardOutput, stdout);
        var errorTask = DrainAsync(pending.StandardError, stderr);
        pending.StandardInput.Close();
        pending.Resume();
        var started = pending.TransferOwnership();
        using var group = started.Group;
        using var process = started.Process;
        using var processHandle = started.ProcessHandle;
        using var output = started.StandardOutput;
        using var error = started.StandardError;
        // Cleanup disposes the group. Keep a non-inherited observer alive until FINAL
        // accounting has been read after termination and the empty-job confirmation.
        var retained = group.TryDuplicateAccountingHandle(out var accountingObserver);
        using var retainedAccounting = accountingObserver;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var state = "exited";
        string? reason = null;
        int? exit = null;
        ExitMembership? exitInspection = null;
        var emptyConfirmed = false;
        try
        {
            while (!process.HasExited)
            {
                Observe(group, observations);
                Observation.Value?.Observer.Invoke(group, retainedAccounting, observations.Count);
                await Task.Delay(ObservationIntervalMilliseconds, deadline.Token).ConfigureAwait(false);
            }
            exit = OwnedProcessGroup.ReadProcessExitCode(processHandle);
            Observe(group, observations);
            if (!group.TryGetActiveProcessIds(out var active))
            { state = "apparatus-failed"; reason = "Job membership unavailable after procedure exit."; }
            else
            {
                exitInspection = InspectExitedJob(group, process, active);
                if (exitInspection.UnverifiedProcessIds.Count > 0)
                { state = "apparatus-failed"; reason = "Job member liveness unavailable after procedure exit."; }
                else if (exitInspection.LiveDescendants.Count > 0)
                { state = "apparatus-failed"; reason = "Procedure exited with live descendants."; }
            }
        }
        catch (OperationCanceledException)
        { state = cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out"; reason = state; }
        finally
        {
            emptyConfirmed = group.TerminateAndConfirmEmpty(TimeSpan.FromSeconds(30));
            if (!emptyConfirmed)
            { state = "apparatus-failed"; reason = "Owned process cleanup did not confirm an empty job."; }
        }
        await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        elapsed.Stop();
        uint total = 0;
        var counted = emptyConfirmed && OwnedProcessGroup.TryGetTotalProcesses(retainedAccounting, out total);
        var complete = counted && total == observations.Count;
        var priorityValid = observations.Values.All(o => o.BelowNormal);
        var launches = observations.Values.GroupBy(p => p.Image, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var executableIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in launches.Keys)
        {
            try { executableIdentities[image] = ExperimentReplayStore.Source(image).Sha256 + ":" + FileVersionInfo.GetVersionInfo(image).FileVersion; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; }
        }
        var accountingRead = OwnedProcessGroup.TryReadAccounting(retainedAccounting, out var accounting);
        var evidence = new ExperimentReplayAccountingEvidence(assignedBeforeResume, true, retained, emptyConfirmed,
            emptyConfirmed && accountingRead, counted);
        var resources = accountingRead
            ? new ExperimentReplayResources(accounting.CpuMilliseconds, accounting.PeakMemoryBytes,
                elapsed.Elapsed.TotalMilliseconds, launches, "job-object/" + accounting.AccountingSource, complete, total, executableIdentities,
                evidence, new(ObservationIntervalMilliseconds, "pid+creation-time", "held-handle-job-membership", scheduling.IsBelowNormal, priorityValid)) : null;
        var accountingFailure = AccountingFailure(resources);
        if (accountingFailure is not null && state != "apparatus-failed")
        { state = "apparatus-failed"; reason = accountingFailure; }
        if (!priorityValid) { state = "apparatus-failed"; reason = "Observed descendant exceeded registered below-normal priority."; }
        if (state == "exited" && resources?.AllChildPrioritiesVerified != true)
            reason = "All-child below-normal priority unverified; root-applied policy and observed member priorities only.";
        return new(exit, state, reason, scheduling.PriorityClass ?? "unknown",
            $"control={scheduling.ControlMask};state={scheduling.StateMask};unavailable={scheduling.UnavailableReason}", resources, stdout, stderr)
        { ExitInspection = exitInspection };
    }

    internal static string? AccountingFailure(ExperimentReplayResources? resources) => resources is null
        ? "Job accounting unavailable." : resources.AggregateAccountingValid ? null : "Job accounting evidence unavailable or contradictory.";

    internal static ExitMembership InspectExitedJob(OwnedProcessGroup group, Process root, IReadOnlyList<int> reportedIds)
    {
        if (!root.HasExited) throw new InvalidOperationException("Exit inspection requires an exited procedure root.");
        var live = new List<MemberIdentity>();
        var unverified = new List<int>();
        foreach (var id in reportedIds)
        {
            // The retained root process/handle pins this identity through cleanup. Job PID
            // lists can still include it after exit; membership alone is not liveness.
            if (id == root.Id) continue;
            try
            {
                using var member = Process.GetProcessById(id);
                var handle = member.SafeHandle;
                if (!group.Contains(handle) || member.HasExited) continue;
                var identity = WindowsNativeProcessInspection.ReadLifecycleIdentity(id, handle);
                if (identity is { Status: ProcessInspectionStatus.Available, StartedAt: { } start, ExecutablePath: { Length: > 0 } path })
                {
                    // Bind membership, identity and liveness to the same held handle,
                    // including a final check after reading identity (PIDs can be reused).
                    if (group.Contains(handle) && !member.HasExited)
                        live.Add(new(id, start, path));
                }
                else if (identity.Status is not (ProcessInspectionStatus.Exited or ProcessInspectionStatus.DeadOrRecycled))
                    unverified.Add(id);
            }
            catch (ArgumentException) { } // This listed process no longer exists.
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { unverified.Add(id); }
        }
        return new(root.Id, root.StartTime.ToUniversalTime(), reportedIds.ToArray(), live, unverified);
    }

    private static void Observe(OwnedProcessGroup group, IDictionary<(int, long), (string Image, bool BelowNormal)> observations)
    {
        if (!group.TryGetActiveProcessIds(out var ids)) return;
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (!group.Contains(process.SafeHandle)) continue;
                var identity = WindowsNativeProcessInspection.ReadLifecycleIdentity(id, process.SafeHandle);
                if (identity is { Status: ProcessInspectionStatus.Available, StartedAt: { } start, ExecutablePath: { Length: > 0 } path })
                {
                    var below = process.PriorityClass is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle;
                    if (!group.Contains(process.SafeHandle)) continue;
                    if (Observation.Value?.IncludeExecutable is { } include && !include(path)) continue;
                    var key = (id, start.UtcTicks);
                    if (observations.TryGetValue(key, out var old)) below &= old.BelowNormal;
                    observations[key] = (path, below);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, string path)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        var buffer = new char[8192];
        var retained = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var keep = Math.Min(count, Math.Max(0, 1024 * 1024 - retained));
            if (keep > 0) { await writer.WriteAsync(buffer.AsMemory(0, keep)).ConfigureAwait(false); retained += keep; }
        }
    }
}
