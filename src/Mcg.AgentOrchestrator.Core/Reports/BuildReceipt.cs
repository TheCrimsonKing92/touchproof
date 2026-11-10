using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

// One local build observation; unknown CPU is never represented as zero.
public sealed record BuildReceipt(
    [property: JsonRequired] DateTimeOffset RecordedAt,
    [property: JsonRequired] string? GoalId,
    [property: JsonRequired] string Origin,
    [property: JsonRequired] string TreeDigest,
    [property: JsonRequired] string[] Projects,
    [property: JsonRequired] double WallMs,
    [property: JsonRequired] double? CpuMs,
    [property: JsonRequired] string? CpuMsUnavailableReason,
    [property: JsonRequired] string Cache,
    [property: JsonRequired] int Version = 1)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static bool TryParse(string line, out BuildReceipt? receipt)
    {
        receipt = null;
        try
        {
            var value = JsonSerializer.Deserialize<BuildReceipt>(line, Json);
            if (value is null || value.Version != 1 || value.RecordedAt == default ||
                value.Origin is not ("worker-check" or "pre-review-evidence" or "gate" or "train" or "managed-rebuild") ||
                value.Cache is not ("hit" or "miss" or "none") || string.IsNullOrWhiteSpace(value.TreeDigest) ||
                value.Projects is null || value.Projects.Any(string.IsNullOrWhiteSpace) ||
                !double.IsFinite(value.WallMs) || value.WallMs < 0 ||
                (value.CpuMs is { } cpu && (!double.IsFinite(cpu) || cpu < 0)) ||
                (value.CpuMs is null && string.IsNullOrWhiteSpace(value.CpuMsUnavailableReason))) return false;
            receipt = value;
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static string Digest(string? tree) => string.IsNullOrWhiteSpace(tree) || tree is "unresolved" or "unavailable"
        ? "unresolved" : tree.StartsWith("wbc1:git-tree:", StringComparison.Ordinal) ? tree : "wbc1:git-tree:" + tree;
}
