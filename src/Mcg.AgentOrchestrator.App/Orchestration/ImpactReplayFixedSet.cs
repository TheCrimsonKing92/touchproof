using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ImpactReplayFixedSet
{
    private ImpactReplayFixedSet(int version, IReadOnlyList<string> attemptIds)
    {
        Version = version;
        AttemptIds = attemptIds;
    }

    internal int Version { get; }
    internal IReadOnlyList<string> AttemptIds { get; }

    internal static ImpactReplayFixedSet Read(string path)
    {
        using var stream = File.OpenRead(path);
        try
        {
            using var document = JsonDocument.Parse(stream);
            return Parse(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"Malformed attempts file JSON: {ex.Message}", ex);
        }
    }

    private static ImpactReplayFixedSet Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The attempts file must contain a JSON object.");
        if (!root.TryGetProperty("version", out var versionProperty))
            throw new InvalidDataException("Missing version.");
        if (versionProperty.ValueKind != JsonValueKind.Number || !versionProperty.TryGetInt32(out var version))
            throw new InvalidDataException("The version must be an integer.");
        if (version <= 0)
            throw new InvalidDataException("The version must be positive.");

        if (!root.TryGetProperty("attemptIds", out var idsProperty))
            throw new InvalidDataException("Missing attemptIds.");
        if (idsProperty.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The attemptIds value must be an array.");
        if (idsProperty.GetArrayLength() == 0)
            throw new InvalidDataException("The attemptIds array is empty.");

        var attemptIds = new List<string>(idsProperty.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in idsProperty.EnumerateArray())
        {
            var id = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidDataException($"Blank or non-string attempt id at attemptIds[{attemptIds.Count}].");
            if (!seen.Add(id))
                throw new InvalidDataException($"Duplicate attempt id '{id}'.");
            attemptIds.Add(id);
        }
        return new ImpactReplayFixedSet(version, attemptIds.AsReadOnly());
    }
}
