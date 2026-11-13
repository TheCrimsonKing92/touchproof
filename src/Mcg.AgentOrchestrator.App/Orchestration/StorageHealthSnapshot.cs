using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One completed retention measurement, captured before replacing the previous snapshot.
internal sealed record StorageHealthSnapshot(long TotalBytes, long TotalFiles, int OverBudgetCount,
    int UnregisteredCount, string? LargestOverBudgetClass, DateTimeOffset MeasuredAtUtc,
    IReadOnlyList<AcceptanceHeldEvidenceRow>? HeldAcceptanceEvidence = null)
{
    internal string Serialize() => JsonSerializer.Serialize(this with
    {
        HeldAcceptanceEvidence = (HeldAcceptanceEvidence ?? []).OrderBy(row => row.Action, StringComparer.Ordinal)
            .ThenBy(row => row.Reason, StringComparer.Ordinal).ToArray()
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    internal static StorageHealthSnapshot? Parse(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !Nonnegative(root, "totalBytes", out var bytes) ||
            !Nonnegative(root, "totalFiles", out var files) ||
            !Nonnegative(root, "overBudgetCount", out var over) || over > int.MaxValue ||
            !Nonnegative(root, "unregisteredCount", out var unregistered) || unregistered > int.MaxValue ||
            !root.TryGetProperty("largestOverBudgetClass", out var offender) ||
            offender.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            !root.TryGetProperty("measuredAtUtc", out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out var measuredAt) ||
            measuredAt == DateTimeOffset.MinValue)
            return null;

        var name = offender.GetString();
        if (over == 0 ? name is not null : string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\r', '\n']) >= 0)
            return null;
        var rows = new List<AcceptanceHeldEvidenceRow>();
        if (root.TryGetProperty("heldAcceptanceEvidence", out var held))
        {
            if (held.ValueKind != JsonValueKind.Array) return null;
            foreach (var row in held.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String ||
                    !Enum.GetNames<EvidenceRetentionAction>().Contains(action.GetString(), StringComparer.Ordinal) ||
                    action.GetString() == nameof(EvidenceRetentionAction.Deleted) ||
                    !row.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String ||
                    !Nonnegative(row, "decisionCount", out var count) || count > int.MaxValue ||
                    !Nonnegative(row, "bytes", out var heldBytes) ||
                    !Nonnegative(row, "unmeasured", out var unmeasured) || unmeasured > int.MaxValue)
                    return null;
                rows.Add(new(action.GetString()!, reason.GetString()!, (int)count, heldBytes, (int)unmeasured));
            }
        }
        return new(bytes, files, (int)over, (int)unregistered, name, measuredAt.ToUniversalTime(), rows);
    }

    private static bool Nonnegative(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetInt64(out value) && value >= 0;
    }
}
