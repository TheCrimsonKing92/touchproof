using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal enum ShadowStartPhase { Unspecified, Copying, LaunchPending, Running, Released, Reclaimed }

internal sealed record ShadowStartRun(Guid Id, ShadowScratchRetention Intent, string Source, string Scratch,
    string State, string Staging, string[] Databases, int OwnerPid, DateTimeOffset OwnerStarted,
    ShadowStartPhase Phase, ShadowFileIdentity? ScratchIdentity = null, ShadowFileIdentity? StateIdentity = null,
    ShadowFileIdentity? StagingIdentity = null, string? CleanupReason = null,
    int? CandidatePid = null, DateTimeOffset? CandidateStarted = null)
{
    internal string JobName => $@"Global\McgShadowStart-{Id:N}";
}

internal sealed record ShadowStartMeasurement(string? Assertion, string? Detail, long? WallMs = null,
    long? PeakWorkingSetBytes = null, long? FirstTickMs = null, string[]? OutputTail = null,
    bool ReleaseVerified = false);

internal sealed record ShadowStartCleanup(string Status, string? Reason, long Milliseconds, string Receipt);

internal sealed class ShadowStartRunRegistry : IDisposable
{
    private readonly string _directory;
    private readonly FileStream _lease;

    internal ShadowStartRunRegistry(string directory)
    {
        _directory = Directory.CreateDirectory(directory).FullName;
        _lease = new FileStream(Path.Combine(_directory, "run.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    internal string RecordPath(Guid id) => Path.Combine(_directory, $"{id:N}.run.json");
    internal string ReceiptPath(Guid id) => Path.Combine(_directory, $"{id:N}.receipt.json");
    internal void Save(ShadowStartRun run) => WriteJson(RecordPath(run.Id), run);

    internal void Recover(string protectedSource, TextWriter diagnostics)
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*.run.json"))
        {
            try
            {
                var run = JsonSerializer.Deserialize<ShadowStartRun>(File.ReadAllText(path));
                if (run is null || !PathsEqual(RecordPath(run.Id), path) || !IsValid(run))
                    throw new IOException("Invalid disposable ownership record.");
                if (run.Phase == ShadowStartPhase.Reclaimed) continue;

                string? reason = null;
                if (Overlaps(protectedSource, run.Scratch) || Overlaps(protectedSource, run.Staging))
                    reason = "protected-source";
                else if (run.Phase != ShadowStartPhase.Released)
                {
                    var pidProbe = new ConductLockPidProbe();
                    var owner = pidProbe.Probe(run.OwnerPid, run.OwnerStarted);
                    if (owner != ConductLockProbeOutcome.Gone)
                        reason = "supervisor-live-or-unverifiable";
                    else if (run.Phase != ShadowStartPhase.Copying &&
                             (run.Phase != ShadowStartPhase.Running ||
                              run.CandidatePid is not > 0 || run.CandidateStarted is null ||
                              pidProbe.Probe(run.CandidatePid.Value, run.CandidateStarted) != ConductLockProbeOutcome.Gone ||
                              !OwnedProcessGroup.IsNamedJobConfirmedEmpty(run.JobName)))
                        reason = "process-group-unconfirmed";
                }

                var result = Cleanup(run, reason, measurement: null, capacity: null, candidate: null);
                if (result.Status == "deferred")
                    diagnostics.WriteLine($"SHADOW_START recovery=deferred run_id={run.Id:N} cleanup_reason={result.Reason}");
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                diagnostics.WriteLine($"SHADOW_START recovery=deferred record={Path.GetFileName(path)} reason={ex.Message}");
            }
        }
    }

    internal ShadowStartCleanup Cleanup(ShadowStartRun run, string? reason, ShadowStartMeasurement? measurement,
        IReadOnlyList<ShadowStartCapacity>? capacity, string? candidate)
    {
        var elapsed = Stopwatch.StartNew();
        var receipt = ReceiptPath(run.Id);
        JsonObject? document = null;
        try
        {
            if (reason is null && !IsValid(run))
                reason = "containment";
            if (measurement is not null || !File.Exists(receipt))
            {
                document = JsonSerializer.SerializeToNode(new
                {
                    run_id = run.Id.ToString("N"), mode = "disposable", source = run.Source, scratch = run.Scratch,
                    candidate, candidate_pid = run.CandidatePid, candidate_started = run.CandidateStarted,
                    capacity, result = measurement is null ? "interrupted" :
                        measurement.Assertion is null ? "pass" : "fail",
                    assertion = measurement?.Assertion, detail = measurement?.Detail, wall_ms = measurement?.WallMs,
                    peak_working_set_bytes = measurement?.PeakWorkingSetBytes, first_tick_ms = measurement?.FirstTickMs,
                    output_tail = measurement?.OutputTail,
                    journal_directory = Path.Combine(run.State, "logs"),
                    journal_tail = reason is null && measurement is not null ? ReadJournalTail(run.State) : null,
                    cleanup = "pending"
                })!.AsObject();
                WriteJson(receipt, document);
            }
            else
                document = JsonNode.Parse(File.ReadAllText(receipt)) as JsonObject
                    ?? throw new IOException("Invalid measurement receipt.");
            if (reason is null)
            {
                if (!OperatingSystem.IsWindows())
                    reason = "release-unverifiable-platform";
                else
                {
                    // Save the proof first so a crash during deletion can retry without a vanished job handle.
                    run = run with { Phase = ShadowStartPhase.Released };
                    Save(run);
                    ShadowStartReclaimer.DeleteStores(run);
                    run = run with { Phase = ShadowStartPhase.Reclaimed };
                }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 32 or 33)
        {
            reason = "sharing-violation";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException)
        {
            reason = $"io:{ex.Message}";
        }

        var cleanup = new ShadowStartCleanup(reason is null ? "reclaimed" : "deferred", reason,
            elapsed.ElapsedMilliseconds, receipt);
        try
        {
            if (document is not null)
            {
                document["cleanup"] = cleanup.Status;
                document["cleanup_reason"] = cleanup.Reason;
                document["cleanup_ms"] = cleanup.Milliseconds;
                WriteJson(receipt, document);
            }
            Save(run with { CleanupReason = reason });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return cleanup with { Status = "deferred", Reason = $"receipt-write:{ex.Message}" };
        }
        return cleanup;
    }

    private static Dictionary<string, string> ReadJournalTail(string state)
    {
        const int maxBytesPerFile = 64 * 1024;
        var logs = Path.Combine(state, "logs");
        var journal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(logs) || File.GetAttributes(logs).HasFlag(FileAttributes.ReparsePoint))
            return journal;
        foreach (var path in Directory.EnumerateFiles(logs, "conduct-events*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(16))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - maxBytesPerFile), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var buffer = new char[maxBytesPerFile];
            var length = reader.ReadBlock(buffer);
            journal.Add(Path.GetFileName(path), new string(buffer, 0, length));
        }
        return journal;
    }

    private bool IsValid(ShadowStartRun run) =>
        run.Id != Guid.Empty && run is { Intent: ShadowScratchRetention.Disposable, OwnerPid: > 0 } &&
        run.OwnerStarted != default &&
        run.Phase is >= ShadowStartPhase.Copying and <= ShadowStartPhase.Reclaimed &&
        IsCanonical(run.Source) && IsCanonical(run.Scratch) && IsCanonical(run.State) && IsCanonical(run.Staging) &&
        PathsEqual(run.State, Path.Combine(run.Scratch, ".orchestrator")) &&
        PathsEqual(Path.GetFileName(run.Staging), run.Id.ToString("N")) &&
        !Overlaps(run.Source, run.Scratch) && !Overlaps(run.Source, run.Staging) &&
        !Overlaps(run.Scratch, run.Staging) && !Overlaps(_directory, run.Scratch) &&
        !Overlaps(_directory, run.Staging) && !Overlaps(_directory, run.Source) &&
        run.Databases is not null && run.Databases.Distinct(StringComparer.OrdinalIgnoreCase).Count() == run.Databases.Length &&
        run.Databases.All(name => ShadowStartCopyPlan.DatabaseNames.Contains(name, StringComparer.Ordinal));

    private static bool IsCanonical(string? path) => path is not null && Path.IsPathFullyQualified(path) &&
        !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal) &&
        PathsEqual(Path.GetFullPath(path), path);

    internal static bool Overlaps(string first, string second) =>
        CliShadowStartCommand.ContainsPath(first, second) || CliShadowStartCommand.ContainsPath(second, first);

    private static bool PathsEqual(string first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void WriteJson<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public void Dispose() => _lease.Dispose();
}
