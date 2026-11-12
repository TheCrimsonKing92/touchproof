using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One completed retention measurement, captured before replacing the previous snapshot.
internal sealed record StorageHealthSnapshot(long TotalBytes, long TotalFiles, int OverBudgetCount,
    int UnregisteredCount, string? LargestOverBudgetClass, DateTimeOffset MeasuredAtUtc)
{
    internal string Serialize() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web));

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
        return new(bytes, files, (int)over, (int)unregistered, name, measuredAt.ToUniversalTime());
    }

    private static bool Nonnegative(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetInt64(out value) && value >= 0;
    }
}
