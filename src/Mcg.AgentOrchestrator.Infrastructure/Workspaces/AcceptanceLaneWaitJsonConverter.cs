using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Existing typed receipt readers must not reject an attempt/verdict because its advisory
// timing extension is damaged. The waits reader separately salvages individual observations.
public sealed class AcceptanceLaneWaitJsonConverter : JsonConverter<IReadOnlyList<AcceptanceLaneWaitOccurrence>>
{
    public override IReadOnlyList<AcceptanceLaneWaitOccurrence>? Read(ref Utf8JsonReader reader,
        Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        var rows = new List<AcceptanceLaneWaitOccurrence>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            try
            {
                if (row.Deserialize<AcceptanceLaneWaitOccurrence>(options) is { } occurrence)
                    rows.Add(occurrence);
            }
            catch (JsonException) { /* Damaged optional occurrence, no verdict authority. */ }
        }
        return rows;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<AcceptanceLaneWaitOccurrence> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var row in value) JsonSerializer.Serialize(writer, row, options);
        writer.WriteEndArray();
    }
}
