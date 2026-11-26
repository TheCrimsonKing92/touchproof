using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal enum ShadowStartPhase { Unspecified, Copying, LaunchPending, Running, Released, Reclaimed }

// Older StoreIdentities maps may contain sidecars adopted during cleanup.
// Only CreatedSidecarIdentities proves creation before launch.
internal sealed record ShadowStartRun(Guid Id, ShadowScratchRetention Intent, string Source, string Scratch,
    string State, string Staging, string[] Databases, int OwnerPid, DateTimeOffset OwnerStarted,
    ShadowStartPhase Phase, ShadowFileIdentity? ScratchIdentity = null, ShadowFileIdentity? StateIdentity = null,
    ShadowFileIdentity? StagingIdentity = null, string? CleanupReason = null,
    int? CandidatePid = null, DateTimeOffset? CandidateStarted = null,
    Dictionary<string, ShadowFileIdentity>? StoreIdentities = null, string? CandidateImagePath = null,
    Dictionary<string, ShadowFileIdentity>? CreatedSidecarIdentities = null)
{
    internal string JobName => $@"Global\McgShadowStart-{Id:N}";
}

internal sealed record ShadowStartMeasurement(string? Assertion, string? Detail, long? WallMs = null,
    long? PeakWorkingSetBytes = null, long? FirstTickMs = null, string[]? OutputTail = null,
    bool ReleaseVerified = false);

internal sealed record ShadowStartCleanup(string Status, string? Reason, long Milliseconds, string Receipt);

internal sealed record ShadowStartSidecarCreation(Guid RunId, SpawnProcessIdentity Creator, string Path,
    ShadowFileIdentity Identity);

internal sealed class ShadowStartRunRegistry : IDisposable
{
    private const string SettlementSuffix = ".settlement.json";
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
    internal string SettlementPath(Guid id) => Path.Combine(_directory, $"{id:N}{SettlementSuffix}");
    internal void Save(ShadowStartRun run) => WriteJson(RecordPath(run.Id), run);

    internal static ShadowStartRun ReadCandidateRun(string path, string state, SpawnProcessIdentity creator)
    {
        if (!IsCanonical(path))
            throw new IOException("Invalid shadow-start sidecar registration path.");
        var run = JsonSerializer.Deserialize<ShadowStartRun>(File.ReadAllText(path));
        if (run is not { Phase: ShadowStartPhase.Running } ||
            !IsValid(run, Path.GetDirectoryName(path)!) || !PathsEqual(run.State, state) ||
            !PathsEqual(Path.GetFileName(path), $"{run.Id:N}.run.json") ||
            ReadCandidateIdentity(run) is not { } candidate || !ProcessImageResolution.SameProcess(candidate, creator))
            throw new IOException("Shadow-start sidecar registration does not match the running candidate.");
        return run;
    }

    internal static void SaveCandidateSidecar(string record, ShadowStartSidecarCreation creation) =>
        WriteJson(CandidateSidecarPath(record, creation.Path), creation);

    private static string CandidateSidecarPath(string record, string sidecar) =>
        $"{record}.{Path.GetFileName(sidecar)}.creation.json";

    private ShadowFileIdentity? ReadCandidateSidecar(ShadowStartRun run, string path)
    {
        if (ReadCandidateIdentity(run) is not { } candidate) return null;
        var receipt = CandidateSidecarPath(RecordPath(run.Id), path);
        try
        {
            var creation = JsonSerializer.Deserialize<ShadowStartSidecarCreation>(File.ReadAllText(receipt));
            return creation is { Creator: { } creator } && creation.RunId == run.Id && PathsEqual(creation.Path, path) &&
                ProcessImageResolution.IsResolved(creator.ImagePath) &&
                ProcessImageResolution.SameProcess(creator, candidate) ? creation.Identity : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal void Recover(string protectedSource, TextWriter diagnostics)
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*.run.json"))
        {
            try
            {
                var run = JsonSerializer.Deserialize<ShadowStartRun>(File.ReadAllText(path));
                if (run is null || !PathsEqual(RecordPath(run.Id), path) || !IsValid(run, _directory))
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
                    else if (run.Phase != ShadowStartPhase.Copying && !HasCandidateSettlement(run))
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

    private bool HasCandidateSettlement(ShadowStartRun run) =>
        ReadCandidateIdentity(run) is { } identity &&
        DurableAcceptanceJob.HasSettlementReceipt(SettlementPath(run.Id), run.JobName, identity);

    private static SpawnProcessIdentity? ReadCandidateIdentity(ShadowStartRun run) =>
        run is { CandidatePid: { } pid, CandidateStarted: { } started, CandidateImagePath: { } image } &&
        pid > 0 && started != default && Path.IsPathFullyQualified(image) && ProcessImageResolution.IsResolved(image)
            ? new(pid, started, image) : null;

    internal ShadowStartCleanup Cleanup(ShadowStartRun run, string? reason, ShadowStartMeasurement? measurement,
        IReadOnlyList<ShadowStartCapacity>? capacity, string? candidate)
    {
        var elapsed = Stopwatch.StartNew();
        var receipt = ReceiptPath(run.Id);
        JsonObject? document = null;
        try
        {
            if (reason is null && !IsValid(run, _directory))
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
                    ShadowStartReclaimer.DeleteStores(run, path => ReadCandidateSidecar(run, path));
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

    private static bool IsValid(ShadowStartRun run, string registryDirectory) =>
        run.Id != Guid.Empty && run is { Intent: ShadowScratchRetention.Disposable, OwnerPid: > 0 } &&
        run.OwnerStarted != default &&
        run.Phase is >= ShadowStartPhase.Copying and <= ShadowStartPhase.Reclaimed &&
        IsCanonical(run.Source) && IsCanonical(run.Scratch) && IsCanonical(run.State) && IsCanonical(run.Staging) &&
        PathsEqual(run.State, Path.Combine(run.Scratch, ".orchestrator")) &&
        PathsEqual(Path.GetFileName(run.Staging), run.Id.ToString("N")) &&
        !Overlaps(run.Source, run.Scratch) && !Overlaps(run.Source, run.Staging) &&
        !Overlaps(run.Scratch, run.Staging) && !Overlaps(registryDirectory, run.Scratch) &&
        !Overlaps(registryDirectory, run.Staging) && !Overlaps(registryDirectory, run.Source) &&
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
