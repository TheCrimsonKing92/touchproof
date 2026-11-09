using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetBuildStaleLeaseReclaim
{
    private const int StaleLeaseIntegrityProbeAttempts = 3;
    internal static readonly AsyncLocal<Action<int>?> BeforeStaleLeaseIntegrityProbeOverride = new();

    internal static StaleExecutionLeaseReclaim TryReclaimStaleExecutionLease(DotnetBuildEnvironment environment)
    {
        var metadata = DotnetBuildLeaseInspection.TryReadExecutionLeaseMetadata(environment.ExecutionLockPath);
        if (metadata?.OwnerProcessId is not { } processId || DotnetBuildLeaseInspection.IsProcessRunning(processId))
        {
            return StaleExecutionLeaseReclaim.None;
        }

        var integrity = ProbeStaleLeaseArtifacts(environment, metadata.AcquiredAt);
        try
        {
            File.Delete(environment.ExecutionLockPath);
            AcceptanceAttemptArtifactCustody.ClearIfStale(environment.ArtifactsPath);
            DotnetBuildEnvironmentManager.EmitLeaseReceipt("LEASE_RECLAIM", environment, processId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StaleExecutionLeaseReclaim.None;
        }

        return new StaleExecutionLeaseReclaim(true, processId, integrity);
    }

    private static StaleLeaseArtifactIntegrity ProbeStaleLeaseArtifacts(
        DotnetBuildEnvironment environment,
        DateTimeOffset leaseAcquiredAt)
    {
        if (IsEnabled(Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.ForceCleanStaleLeaseArtifactsVariable)))
        {
            return new StaleLeaseArtifactIntegrity(
                true,
                "operator-forced",
                $"env:{DotnetBuildEnvironmentManager.ForceCleanStaleLeaseArtifactsVariable}");
        }

        for (var attempt = 1; attempt <= StaleLeaseIntegrityProbeAttempts; attempt++)
        {
            try
            {
                BeforeStaleLeaseIntegrityProbeOverride.Value?.Invoke(attempt);
                return ProbeStaleLeaseArtifactsOnce(environment, leaseAcquiredAt);
            }
            catch (Exception ex) when (
                (ex is IOException or UnauthorizedAccessException) &&
                attempt < StaleLeaseIntegrityProbeAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(10));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new StaleLeaseArtifactIntegrity(true, "integrity-probe-failed", environment.ArtifactsPath);
            }
        }

        throw new InvalidOperationException("Stale lease integrity probe exhausted without a verdict.");
    }

    private static StaleLeaseArtifactIntegrity ProbeStaleLeaseArtifactsOnce(
        DotnetBuildEnvironment environment,
        DateTimeOffset leaseAcquiredAt)
    {
        var ownerPath = Path.Combine(environment.ArtifactsPath, DotnetBuildEnvironmentManager.ArtifactsOwnerFileName);
        if (!Directory.Exists(environment.ArtifactsPath) ||
            !Directory.EnumerateFileSystemEntries(environment.ArtifactsPath).Any())
        {
            return new StaleLeaseArtifactIntegrity(false, "artifacts-empty", environment.ArtifactsPath);
        }

        if (!File.Exists(ownerPath))
        {
            return new StaleLeaseArtifactIntegrity(true, "missing-owner-marker", ownerPath);
        }

        try
        {
            var marker = JsonSerializer.Deserialize<DotnetBuildEnvironmentManager.ArtifactsOwnerMarker>(File.ReadAllText(ownerPath), DotnetBuildEnvironmentManager.JsonOptions);
            if (marker is null ||
                string.IsNullOrWhiteSpace(marker.OwnerToken) ||
                !string.Equals(marker.OwnerToken, environment.SlotOwnerToken, StringComparison.Ordinal))
            {
                return new StaleLeaseArtifactIntegrity(true, "invalid-owner-marker", ownerPath);
            }
        }
        catch (JsonException)
        {
            return new StaleLeaseArtifactIntegrity(true, "invalid-owner-marker", ownerPath);
        }

        var probeAfterUtc = leaseAcquiredAt == DateTimeOffset.MinValue
            ? DateTime.MinValue
            : leaseAcquiredAt.UtcDateTime.AddSeconds(-2);
        foreach (var dll in new DirectoryInfo(environment.ArtifactsPath)
            .EnumerateFiles("*.dll", SearchOption.AllDirectories)
            .Where(file => file.LastWriteTimeUtc >= probeAfterUtc))
        {
            if (dll.Length == 0)
            {
                return new StaleLeaseArtifactIntegrity(true, "zero-length-dll", dll.FullName);
            }

            if (!HasValidPortableExecutableHeader(dll))
            {
                return new StaleLeaseArtifactIntegrity(true, "invalid-pe-dll", dll.FullName);
            }
        }

        return new StaleLeaseArtifactIntegrity(false, "integrity-ok", ownerPath);
    }

    private static bool HasValidPortableExecutableHeader(FileInfo file)
    {
        using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        try
        {
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var headers = peReader.PEHeaders;
            if (headers.PEHeader is null ||
                headers.PEHeader.SizeOfHeaders > stream.Length)
            {
                return false;
            }

            return headers.SectionHeaders.All(section =>
                section.PointerToRawData >= 0 &&
                section.SizeOfRawData >= 0 &&
                (long)section.PointerToRawData + section.SizeOfRawData <= stream.Length);
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static bool IsEnabled(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    internal static void EmitLeaseReclaimReceipt(
        DotnetBuildEnvironment environment,
        int? reclaimedProcessId,
        StaleLeaseArtifactIntegrity integrity,
        string receipt = "LEASE_RECLAIM",
        string? decision = null)
    {
        var reclaimDecision = decision ?? (integrity.ForceCleanArtifacts ? "wiped" : "preserved");
        var journalPath = Path.Combine(
            Path.GetDirectoryName(environment.ExecutionLockPath)!,
            DotnetBuildLeaseJournal.LeaseJournalFileName);
        var entry = new DotnetBuildLeaseJournal.LeaseJournalEntry(
            1,
            DateTimeOffset.UtcNow,
            receipt,
            Environment.ProcessId,
            DotnetBuildEnvironmentManager.ReceiptSlotName(environment),
            environment.LeaseId,
            reclaimedProcessId,
            reclaimDecision,
            integrity.Reason,
            integrity.TriggerPath);
        var journalWritten = DotnetBuildLeaseJournal.TryAppendLeaseJournal(
            journalPath,
            JsonSerializer.Serialize(entry, DotnetBuildLeaseJournal.LeaseJournalJsonOptions) + Environment.NewLine);

        Console.WriteLine(
            $"{receipt} pid={Environment.ProcessId} slot={DotnetBuildEnvironmentManager.ReceiptSlotName(environment)} lease={environment.LeaseId} reclaimedPid={reclaimedProcessId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} decision={reclaimDecision} reason={integrity.Reason} triggerPath=\"{integrity.TriggerPath}\" journal=\"{journalPath}\" journalStatus={(journalWritten ? "written" : "pending")} path={environment.ExecutionLockPath}");
    }

    internal sealed record StaleExecutionLeaseReclaim(
        bool Reclaimed,
        int? ReclaimedProcessId,
        StaleLeaseArtifactIntegrity? Integrity)
    {
        public bool ForceCleanArtifacts => Integrity?.ForceCleanArtifacts == true;

        public static StaleExecutionLeaseReclaim None { get; } = new(false, null, null);
    }

    internal sealed record GoalLeaseReclaim(int? ReclaimedProcessId, string TriggerPath);

    internal sealed record StaleLeaseArtifactIntegrity(
        bool ForceCleanArtifacts,
        string Reason,
        string TriggerPath);
}
