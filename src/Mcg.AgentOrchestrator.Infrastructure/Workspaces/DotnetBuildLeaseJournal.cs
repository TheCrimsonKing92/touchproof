using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetBuildLeaseJournal
{
    internal const string LeaseJournalFileName = "lease.journal.jsonl";
    private const long LeaseJournalMaxBytes = 1_048_576;
    private static readonly object LeaseJournalDrainGate = new();

    internal static readonly JsonSerializerOptions LeaseJournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static void TryAppendGateSlotWaitJournal(DotnetBuildStorageRoot storageRoot, GateSlotWaitRecord wait)
    {
        var entry = new LeaseJournalEntry(1, DateTimeOffset.UtcNow, GateSlotWaitRecord.EventName,
            Environment.ProcessId, wait.AcquiredSlot?.ToString(CultureInfo.InvariantCulture) ?? "none",
            wait.GateIdentity, null, wait.Outcome, wait.FormatDetails(), "");
        TryAppendLeaseJournal(Path.Combine(storageRoot.RootPath, "build-slots", LeaseJournalFileName),
            JsonSerializer.Serialize(entry, LeaseJournalJsonOptions) + Environment.NewLine);
    }

    internal static void TryAppendBuildSlotHoldJournal(DotnetBuildEnvironment environment, string slot, BuildSlotHoldRecord hold)
    {
        try
        {
            var entry = new LeaseJournalEntry(1, hold.ReleasedAt, BuildSlotHoldRecord.EventName,
                Environment.ProcessId, slot, environment.LeaseId, null,
                BuildSlotHoldRecord.Released, hold.FormatDetails(), environment.ArtifactsPath ?? "");
            TryAppendLeaseJournal(Path.Combine(Path.GetDirectoryName(environment.ExecutionLockPath)!,
                BuildSlotHoldRecord.JournalFileName), JsonSerializer.Serialize(entry, LeaseJournalJsonOptions) + Environment.NewLine);
        }
        catch (Exception) { } // Attribution must never prevent lease disposal.
    }

    internal static bool TryAppendLeaseJournal(string journalPath, string serializedEntry)
    {
        string? pendingPath = null;
        try
        {
            var directory = Path.GetDirectoryName(journalPath)!;
            Directory.CreateDirectory(directory);
            pendingPath = Path.Combine(
                directory,
                $"{Path.GetFileName(journalPath)}.pending-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(pendingPath, serializedEntry);
            DrainPendingLeaseJournalEntries(journalPath);
            return !File.Exists(pendingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine(
                $"LEASE_RECLAIM_JOURNAL_PENDING pid={Environment.ProcessId} journal=\"{journalPath}\" pending=\"{pendingPath ?? "unavailable"}\" error={ex.GetType().Name}");
            return false;
        }
    }

    private static void DrainPendingLeaseJournalEntries(string journalPath)
    {
        lock (LeaseJournalDrainGate)
        {
            using var drainLock = new FileStream(
                journalPath + ".drain.lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            var directory = Path.GetDirectoryName(journalPath)!;
            var pattern = $"{Path.GetFileName(journalPath)}.pending-*.jsonl";
            foreach (var pendingPath in Directory.GetFiles(directory, pattern)
                .OrderBy(ReadPendingLeaseJournalRecordedAt)
                .ThenBy(path => path, StringComparer.Ordinal))
            {
                RotateLeaseJournalIfNeeded(journalPath);
                File.AppendAllText(journalPath, File.ReadAllText(pendingPath));
                File.Delete(pendingPath);
            }
        }
    }

    private static DateTimeOffset ReadPendingLeaseJournalRecordedAt(string pendingPath)
    {
        try
        {
            return JsonSerializer.Deserialize<LeaseJournalEntry>(
                    File.ReadAllText(pendingPath),
                    LeaseJournalJsonOptions)
                ?.RecordedAt ?? DateTimeOffset.MaxValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private static void RotateLeaseJournalIfNeeded(string journalPath)
    {
        if (!File.Exists(journalPath) || new FileInfo(journalPath).Length < LeaseJournalMaxBytes)
        {
            return;
        }

        var directory = Path.GetDirectoryName(journalPath)!;
        var fileName = Path.GetFileNameWithoutExtension(journalPath);
        var extension = Path.GetExtension(journalPath);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var rotatedPath = Path.Combine(directory, $"{fileName}-{stamp}{extension}");
        for (var index = 1; File.Exists(rotatedPath); index++)
        {
            rotatedPath = Path.Combine(directory, $"{fileName}-{stamp}-{index}{extension}");
        }

        File.Move(journalPath, rotatedPath);
    }

    internal sealed record LeaseJournalEntry(
        int Version,
        DateTimeOffset RecordedAt,
        string Event,
        int ProcessId,
        string Slot,
        string LeaseId,
        int? ReclaimedProcessId,
        string Decision,
        string Reason,
        string TriggerPath);
}
