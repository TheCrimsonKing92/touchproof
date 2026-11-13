using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class StorageHealthStatusView(StorageHealthSnapshot? snapshot, DateTimeOffset now)
{
    internal IReadOnlyList<AcceptanceHeldEvidenceRow> HeldAcceptanceEvidence => snapshot?.HeldAcceptanceEvidence ?? [];

    private TimeSpan Age => snapshot is null || snapshot.MeasuredAtUtc > now
        ? TimeSpan.Zero : now - snapshot.MeasuredAtUtc;

    internal static StorageHealthStatusView Read(string orchestratorDirectory, TimeProvider? time = null)
    {
        StorageHealthSnapshot? snapshot = null;
        try
        {
            using var stream = new FileStream(Path.Combine(orchestratorDirectory, StorageHealthSnapshotWriter.FileName),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int maxBytes = 64 * 1024;
            if (stream.Length <= maxBytes)
            {
                var buffer = new byte[maxBytes + 1];
                var count = 0;
                int read;
                while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) > 0)
                    count += read;
                if (count <= maxBytes) snapshot = StorageHealthSnapshot.Parse(buffer.AsMemory(0, count));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or ArgumentException or InvalidOperationException) { }
        return new(snapshot, (time ?? TimeProvider.System).GetUtcNow());
    }

    internal string FormatLine() => snapshot is null ? "storage unknown" :
        $"storage {FormatSize(snapshot.TotalBytes)}, {snapshot.OverBudgetCount} over budget" +
        (snapshot.OverBudgetCount > 0 ? $" ({snapshot.LargestOverBudgetClass})" : "") +
        $", {snapshot.UnregisteredCount} unregistered, snapshot {GoalBoardProjector.FormatAge(Age)} old";

    internal object ToJson() => snapshot is null ? UnknownJson() : new
    {
        status = "ok",
        snapshot.TotalBytes,
        snapshot.TotalFiles,
        snapshot.OverBudgetCount,
        snapshot.LargestOverBudgetClass,
        snapshot.UnregisteredCount,
        snapshot.MeasuredAtUtc,
        ageSeconds = (long)Age.TotalSeconds
    };

    internal static object UnknownJson() => new { status = "unknown" };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + "B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + "KB",
        < 1024 * 1024 * 1024 => (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + "MB",
        _ => (bytes / (1024d * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + "GB"
    };
}
